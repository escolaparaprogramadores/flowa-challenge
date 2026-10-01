# flowa-challenge

This is a challenge by [Coodesh](https://coodesh.com/)

## Sumário

- [Negócio](#negócio)
- [Arquitetura](#arquitetura)
- [Tecnologias](#tecnologias)
- [Estrutura do repositório](#estrutura-do-repositório)
- [Como rodar](#como-rodar)
- [Como testar](#como-testar)
- [Esteira](#esteira)
- [Observabilidade](#observabilidade)
- [Teste de carga](#teste-de-carga)
- [Custo](#custo)
- [Destroy](#destroy)
- [Decisões](#decisões)
- [Limitações conhecidas](#limitações-conhecidas)

## Negócio

## Arquitetura

## Tecnologias

## Estrutura do repositório

| Pasta | O que tem |
|---|---|
| `src/` | Serviços .NET 10 e a lib comum `Flowa.Common` (envelope de resposta e correlation id) |
| `tests/` | Testes automatizados de cada projeto de `src/` |
| `frontend/` | Tela React (Vite + TypeScript) |
| `infra-aws/` | Terraform da infraestrutura |
| `.github/workflows/` | Esteira de CI/CD |
| `docs/` | Contratos, desenho da arquitetura e relatórios |
| `observabilidade/` | Dashboards, monitores e SLOs |

Contratos entre os serviços: [`docs/contracts/routes.md`](docs/contracts/routes.md).

## Como rodar

## Como testar

## Esteira

## Observabilidade

## Teste de carga

## Custo

## Destroy

## Decisões

## Limitações conhecidas
