# Contrato de rotas

Lista fechada das rotas HTTP do flowa-challenge. Mudar uma rota, o serviço dono ou o cache exige
combinar antes com quem consome (frontend, gateway, CloudFront e teste de carga).

## Regras que valem para todas as rotas

- **Formato da resposta:** toda rota responde no envelope abaixo, em sucesso e em erro
  (lib `src/Flowa.Comum`, `Envelope.cs`).

  ```json
  {
    "data": { },
    "error": null,
    "meta": { "correlationId": "0f8fad5b-d9cb-469f-a165-70867728950e", "timestamp": "2026-09-30T21:00:00.000+00:00" }
  }
  ```

  ```json
  {
    "data": null,
    "error": {
      "code": "VALIDATION_ERROR",
      "message": "Um ou mais campos são inválidos.",
      "details": [ { "field": "valor", "message": "O valor mínimo para esta oferta é R$ 1.000,00." } ]
    },
    "meta": { "correlationId": "0f8fad5b-d9cb-469f-a165-70867728950e", "timestamp": "2026-09-30T21:00:00.000+00:00" }
  }
  ```

- **Códigos de erro** (`error.code`): `VALIDATION_ERROR` (400 com `details`), `BAD_REQUEST` (400),
  `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404), `METHOD_NOT_ALLOWED` (405),
  `CONFLICT` (409), `UNPROCESSABLE_ENTITY` (422), `TOO_MANY_REQUESTS` (429),
  `SERVICE_UNAVAILABLE` (503), `INTERNAL_ERROR` (demais 5xx) e `REQUEST_ERROR` (outros 4xx).
  Erro 5xx sempre leva a mensagem genérica, sem detalhe interno nem stack trace.
- **Sem 204:** rota que não devolve dado responde 200 com `data: null`.
- **Correlation id:** header `X-Correlation-Id`. Entra pela requisição (ou é gerado quando falta ou é
  inválido — até 64 caracteres de `A-Z a-z 0-9 . _ -`), volta no header da resposta e em
  `meta.correlationId`, vai em toda chamada HTTP de saída e em todo log. Fila e FIX usam
  `CorrelationId.Atual` / `CorrelationId.Definir`.
- **Cache de 5 min:** feito pelo CloudFront na frente do gateway, só nas rotas marcadas. O
  `X-Correlation-Id` fica **fora** da chave de cache. Carga e simulação não limpam o cache.
- **Dados pessoais:** o CPF vai só no corpo do `POST /api/sessoes`. Nunca em URL, header, chave de
  cache ou log. O cliente é identificado nas rotas pelo `idCliente` (UUID aleatório).

## Serviços

| Serviço | Papel |
|---|---|
| `app-base-administracao-webapi-ecs` | Sessão, conta do cliente e validações: saldo, limite e consulta de estoque |
| `app-base-reservas-webapi-ecs` | Ofertas, estoque das ofertas, reservas, ordens e simulação |
| `app-base-ordergenerator-worker-ecs` | Lê a fila de reservas e envia a ordem em FIX 4.4 (initiator). Sem rota pública |
| `app-base-orderaccumulator-worker-ecs` | Recebe a ordem FIX (acceptor) e responde. Sem rota pública |

## Rotas

| Método | Rota | Serviço dono | Cache 5 min | Para que serve |
|---|---|---|---|---|
| POST | `/api/sessoes` | administracao | não | Login fictício. CPF e senha só no corpo; qualquer valor entra. Devolve `idCliente` (UUID aleatório gerado no primeiro login e guardado no banco; nunca o CPF nem hash dele) |
| GET | `/api/clientes/{idCliente}/conta` | administracao | não | Dados da conta do cliente (tela Configurações → Conta) |
| PUT | `/api/clientes/{idCliente}/conta` | administracao | não | Altera a conta do cliente |
| GET | `/api/clientes/{idCliente}/disponivel` | administracao | não | Saldo e limite disponíveis para reservar (validação do clique em Reservar) |
| GET | `/api/ofertas/{codigo}/estoque` | administracao | não | Consulta estoque: restante da oferta (validação do clique em Reservar) |
| GET | `/api/ofertas` | reservas | **sim** | Lista das ofertas disponíveis (tela Investir) |
| PUT | `/api/ofertas/{codigo}/estoque` | reservas | não | Altera o estoque de uma oferta (tela Configurações → Estoque das ofertas) |
| POST | `/api/reservas` | reservas | não | Recebe a reserva: valida e recusa na hora o que estiver inconsistente; se ok, publica na fila e responde com status `Recebida` |
| GET | `/api/clientes/{idCliente}/ordens?v=` | reservas | **sim** | Lista das ordens do cliente (tela Ordens). A chave de cache inclui `idCliente` e `v` |
| POST | `/api/simulacoes` | reservas | não | Simula um lote de reservas (tela Configurações → Simular reservas) |
| GET | `/api/administracao/health` | administracao | não | Saúde do serviço. Fora do trace e dos painéis |
| GET | `/api/reservas/health` | reservas | não | Saúde do serviço. Fora do trace e dos painéis |
| GET | `/api/ordergenerator/health` | ordergenerator | não | Saúde do worker, só para o health check do container (não passa pelo gateway) |
| GET | `/api/orderaccumulator/health` | orderaccumulator | não | Saúde do worker, só para o health check do container (não passa pelo gateway) |

### Chave versionada da lista de ordens

`v` é a versão da lista de ordens daquele cliente. Depois de reservar, o frontend troca o `v` e a
próxima consulta fura o cache **só daquele cliente**, sem invalidação no CloudFront. Como o `v` é
gerado e onde fica guardado é decisão do serviço `reservas`, mantendo a regra: a chave nunca usa CPF.
