# OrderProcessingAPI — Arquitetura Serverless e Assíncrona com .NET 10 & AWS SAM

O **OrderProcessingAPI** é um microsserviço serverless de alta performance projetado para o processamento assíncrono e desacoplado de pedidos. Desenvolvido em **.NET 10** e orquestrado via **AWS SAM (Serverless Application Model)**, o projeto aplica padrões arquiteturais de resiliência empresarial, garantindo escalabilidade elástica, isolamento de falhas catastróficas (*Poison Messages*), retenção de estado distribuído e separação estrita entre os caminhos de escrita e leitura.

---

## 🏗️ Arquitetura e Fluxo End-to-End

O sistema adota o padrão **API → Queue → Worker** para a escrita, separando a camada de ingestão de requisições (síncrona e de baixa latência) da camada de processamento de regras de negócio (assíncrona). A camada de leitura é isolada em uma função dedicada, aplicando o princípio da **separação de responsabilidades** (CQRS-like) e o **princípio do menor privilégio** em nível de IAM.

```
Cliente HTTP
│
│ POST /orders { "Product": "...", "Quantity": N }                  GET /orders
│                                                                   GET /orders/{orderId}
▼                                                                   ▼
┌──────────────────────────────────────────────────────────────────────┐
│                       API Gateway (REST)                             │ ──► Controle de Throttling integrado (10k RPS)
└──────────┬───────────────────────────────────────────┬───────────────┘
           │ Proxy Integration                         │ Proxy Integration
           ▼                                           ▼
┌──────────────────────────┐                ┌──────────────────────────┐
│   ProducerFunction       │                │   ReaderFunction         │
│   Lambda (.NET 10)       │                │   Lambda (.NET 10)       │
│   - Validação rápida     │                │   - GetItem por orderId  │
│   - Status = PENDING     │                │   - Scan na coleção      │
└──────────┬───────────────┘                └──────────┬───────────────┘
           │ SendMessageAsync                          │ GetItem / Scan
           ▼                                           │
┌──────────────────────────┐                           │
│   OrdersQueue (SQS)      │ ──► Amortecedor (Buffer) contra picos de tráfego
└──────────┬───────────────┘                           │
           │ EventSource Trigger (BatchSize=1)         │
           ▼                                           │
┌──────────────────────────┐                           │
│   ConsumerFunction       │ ──► Lambda (.NET 10) - Processador de Background
│   - Status = PROCESSED   │                           │
└──────────┬───────────────┘                           │
           │ PutItemAsync                              │
           ▼                                           ▼
┌──────────────────────────────────────────────────────────────────────┐
│                  OrdersTable (DynamoDB)                              │ ──► Persistência NoSQL (Modelo Pay-Per-Request)
└──────────────────────────────────────────────────────────────────────┘
```

### 🛡️ Engenharia de Resiliência e Tratamento de Erros

O ciclo de vida das mensagens foi desenhado para mitigar falhas sem degradação do ecossistema:

1. **Roteamento Direto para DLQ (Erros Estruturais):** Se o `ConsumerFunction` interceptar uma mensagem corrompida (*Poison Message*) ou com falha crítica de validação que impossibilite o reprocessamento, ela é serializada e enviada **diretamente à `OrdersQueueDlq` via código**. Isso evita desperdício de ciclos de computação.
2. **Mecanismo de Retentativa via Redrive Policy (Erros Transitórios):** Falhas de rede, indisponibilidade momentânea ou *throttling* no DynamoDB disparam exceções no runtime do .NET. O SQS retém a mensagem, aplica o **Visibility Timeout de 60s** (uma margem de segurança contra o timeout de 30s da Lambda) e tenta novamente até 3 vezes antes de mover a mensagem automaticamente para a DLQ por infraestrutura.

### 🔍 Separação Escrita/Leitura (Write/Read Path Isolation)

A `ReaderFunction` é mantida em uma Lambda independente das funções de escrita, com o objetivo de:

- **Minimizar a superfície de IAM** — possui apenas a policy `DynamoDBReadPolicy`, sem permissões de escrita, exclusão ou acesso a filas.
- **Isolar o blast radius** — um bug ou bottleneck no caminho de leitura não afeta a ingestão de novos pedidos.
- **Permitir escalabilidade independente** — leituras podem escalar separadamente do pipeline de processamento.

---

## 🛠️ Stack Tecnológico e Componentes

* **Backend core:** .NET 10 (C#) com recursos nativos de `ImplicitUsings` e `Nullable` contexts ativados.
* **AWS SDK & Serverless Core:** `Amazon.Lambda.Core`, `Amazon.Lambda.APIGatewayEvents`, `Amazon.Lambda.SQSEvents` e SDKs isolados para SQS e DynamoDB v2.
* **Infraestrutura como Código (IaC):** AWS SAM / CloudFormation.

### Componentes do Sistema

| Recurso | Tipo | Função |
|---|---|---|
| `ProducerFunction` | Lambda + API Gateway `POST /orders` | Valida o payload, gera `OrderId`/`RequestId`, marca `Status=PENDING` e envia para a `OrdersQueue`. Retorna `202 Accepted`. |
| `OrdersQueue` | SQS | Buffer entre Producer e Consumer. `VisibilityTimeout=60s`, com DLQ após 3 tentativas. |
| `ConsumerFunction` | Lambda (trigger SQS, `BatchSize=1`) | Desserializa, valida, marca `Status=PROCESSED` e grava no DynamoDB. Mensagens inválidas (*poison*) vão direto para a DLQ. |
| `ReaderFunction` | Lambda + API Gateway `GET /orders` e `GET /orders/{orderId}` | Consulta o DynamoDB e devolve os pedidos. Caminho único de leitura do sistema. |
| `OrdersTable` | DynamoDB `PAY_PER_REQUEST` | Persistência. PK = `orderId` (String). |
| `OrdersQueueDlq` | SQS DLQ | Captura mensagens com falha (3+ tentativas) e poison messages. Retenção de 14 dias. |
| `Model` | Class library compartilhada | DTO `Order` usado pelas três Lambdas. |

### Detalhes do ReaderFunction

A `ReaderFunction` implementa dois caminhos no mesmo handler, diferenciando-os pela presença de `orderId` em `PathParameters`:

- **`GET /orders/{orderId}`** — Executa `GetItemAsync` direto pela chave primária. Operação O(1) e barata.
- **`GET /orders`** — Executa `ScanAsync` na tabela inteira. Aceitável no volume atual, mas ineficiente em escala.

A conversão de `Dictionary<string, AttributeValue>` (formato nativo do DynamoDB) para o DTO `Order` é feita no método `MapToOrder`, retornando ao cliente um JSON limpo em vez da estrutura crua de atributos (`{"S":"..."}`, `{"N":"..."}`, etc.).

### Estrutura do Projeto

```
OrderProcessingAPI/
├── OrderProcessingAPI.sln          # Solution clássica (.NET)
├── template.yaml                   # Infraestrutura como Código (SAM)
├── samconfig.toml                  # Configurações padrão do SAM CLI
├── omnisharp.json                  # Configuração de ambiente do editor
├── README.md                       # Documentação do projeto
│
├── src/
│   ├── Model/                      # DTO compartilhado (Order.cs)
│   ├── Producer/                   # Handler Lambda da API de Entrada (POST)
│   ├── Consumer/                   # Handler Lambda do Consumidor da Fila
│   └── Reader/                     # Handler Lambda da API de Leitura (GET)
│
└── events/                         # Payloads JSON de teste local
```

---

## 💻 Desenvolvimento Local e Testes

O projeto conta com fixtures estruturadas na pasta `events/` para simular requisições completas de produção no ambiente local via AWS SAM CLI.

### Pré-requisitos

* .NET 10 SDK
* AWS SAM CLI instalado e configurado
* AWS CLI instalado e configurado

### Comandos Úteis

```bash
# 1. Compilar a Solution do .NET 10 (Release)
dotnet build OrderProcessingAPI.sln -c Release

# 2. Executar o Build do SAM (Gera a árvore compilada em .aws-sam/build/)
sam build

# 3. Validar a Infraestrutura (Linting de CloudFormation)
sam validate --lint

# 4. Testar o Produtor de Pedidos Localmente (Cenário Válido)
sam local invoke ProducerFunction --event events/api-event.json --env-vars env.json

# 5. Testar o Consumidor Localmente (Simulando Payload Corrompido)
sam local invoke ConsumerFunction --event events/sqs-error-event.json --env-vars env.json

# 6. Testar o Reader Localmente (Consulta por ID)
sam local invoke ReaderFunction --event events/api-get-by-id-event.json --env-vars env.json

# 7. Testar o Reader Localmente (Listagem completa)
sam local invoke ReaderFunction --event events/api-get-all-event.json --env-vars env.json

# 8. Inicializar o API Gateway Localmente (Porta 3000)
sam local start-api --env-vars env.json

# 9. Realizar uma requisição de escrita contra o endpoint local
curl -X POST http://localhost:3000/orders \
  -H "Content-Type: application/json" \
  -d '{"Product":"Notebook Dell XPS","Quantity":2}'

# 10. Realizar uma requisição de leitura contra o endpoint local
curl http://localhost:3000/orders
curl http://localhost:3000/orders/{orderId}
```

---
