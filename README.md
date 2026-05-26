# OrderProcessingAPI — Arquitetura Serverless e Assíncrona com .NET 10 & AWS SAM

O **OrderProcessingAPI** é um microsserviço serverless de alta performance projetado para o processamento assíncrono e desacoplado de pedidos. Desenvolvido em **.NET 10** e orquestrado via **AWS SAM (Serverless Application Model)**, o projeto aplica padrões arquiteturais de resiliência empresarial, garantindo escalabilidade elástica, isolamento de falhas catastróficas (*Poison Messages*) e retenção de estado distribuído.

---

## 🏗️ Arquitetura e Fluxo End-to-End

O sistema adota o padrão **API → Queue → Worker**, separando a camada de ingestão de requisições (síncrona e de baixa latência) da camada de processamento de regras de negócio (assíncrona).

```
Cliente HTTP
│
│ POST /orders { "Product": "...", "Quantity": N }
▼
┌──────────────────────────┐
│   API Gateway (REST)     │ ──► Controle de Throttling integrado (10k RPS)
└──────────┬───────────────┘
│ Proxy Integration
▼
┌──────────────────────────┐
│   ProducerFunction       │ ──► Lambda (.NET 10) - Validação rápida e enriquecimento
│   - Status = PENDING     │
└──────────┬───────────────┘
│ SendMessageAsync
▼
┌──────────────────────────┐
│   OrdersQueue (SQS)      │ ──► Amortecedor (Buffer) contra picos de tráfego
└──────────┬───────────────┘
│ EventSource Trigger (BatchSize=1)
▼
┌──────────────────────────┐
│   ConsumerFunction       │ ──► Lambda (.NET 10) - Processador de Background
│   - Status = PROCESSED   │
└──────────┬───────────────┘
│ PutItemAsync
▼
┌──────────────────────────┐
│   OrdersTable (DynamoDB) │ ──► Persistência NoSQL (Modelo Pay-Per-Request)
└──────────────────────────┘
```
### 🛡️ Engenharia de Resiliência e Tratamento de Erros

O ciclo de vida das mensagens foi desenhado para mitigar falhas sem degradação do ecossistema:
1. **Roteamento Direto para DLQ (Erros Estruturais):** Se o `ConsumerFunction` interceptar uma mensagem corrompida (*Poison Message*) ou com falha crítica de validação que impossibilite o reprocessamento, ela é serializada e enviada **diretamente à `OrdersQueueDlq` via código**. Isso evita desperdício de ciclos de computação.
2. **Mecanismo de Retentativa via Redrive Policy (Erros Transitórios):** Falhas de rede, indisponibilidade momentânea ou *throttling* no DynamoDB disparam exceções no runtime do .NET. O SQS retém a mensagem, aplica o **Visibility Timeout de 60s** (uma margem de segurança contra o timeout de 30s da Lambda) e tenta novamente até 3 vezes antes de mover a mensagem automaticamente para a DLQ por infraestrutura.

---

## 🛠️ Stack Tecnológico e Componentes

* **Backend core:** .NET 10 (C#) com recursos nativos de `ImplicitUsings` e `Nullable` contexts ativados.
* **AWS SDK & Serverless Core:** `Amazon.Lambda.Core`, `Amazon.Lambda.APIGatewayEvents`, `Amazon.Lambda.SQSEvents` e SDKs isolados para SQS e DynamoDB v2.
* **Infraestrutura como Código (IaC):** AWS SAM / CloudFormation.

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
│   ├── Model/                      # DTO partilhado (Order.cs)
│   ├── Producer/                   # Handler Lambda da API de Entrada
│   └── Consumer/                   # Handler Lambda do Consumidor da Fila
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

# 6. Inicializar o API Gateway Localmente (Porta 3000)
sam local start-api --env-vars env.json

# 7. Realizar uma requisição de teste real contra o endpoint local
curl -X POST http://localhost:3000/orders \
  -H "Content-Type: application/json" \
  -d '{"Product":"Notebook Dell XPS","Quantity":2}'