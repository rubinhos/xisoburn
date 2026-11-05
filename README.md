# xISOBurn

App para Xbox Series X (UWP, Modo Desenvolvedor) que lê o disco do drive óptico do console, setor
por setor, e envia a imagem pela rede local para um receptor no PC, que grava uma ISO.

> **Estado atual: Marco 2.** O app copia o disco inteiro para um arquivo local no Xbox (LocalState).
> Ele tenta de novo por bloco e por setor, e gera um mapa de setores ruins e o SHA-256. Ainda não há rede.
> - Compilar, instalar e o resultado do teste de viabilidade: [docs/spike.md](docs/spike.md)
> - Como testar a leitura completa e o `config.json`: [docs/marco2.md](docs/marco2.md)

## Estrutura

| Pasta | Conteúdo |
|-------|----------|
| `xbox-app/` | Projeto UWP em C# (`xISOBurn.sln`) |
| `xbox-app/xISOBurn/Scsi/` | Acesso ao drive: `Windows.Devices.Custom` + SCSI pass-through |
| `xbox-app/xISOBurn/Core/` | Detecção do disco, leitura com novas tentativas, mapa de erros, cópia |
| `xbox-app/xISOBurn/Spike/` | Roteiro do teste de viabilidade |
| `xbox-app/tools/` | Script que gera os ícones do pacote |
| `docs/` | Anotações e resultados do spike |
| `.github/workflows/build.yml` | Compilação e empacotamento no GitHub Actions |
| `pc-receiver/` | Receptor no PC (Marco 3) |

## Compilação

Tudo é compilado no GitHub Actions; não é preciso instalar nada no PC. Cada push para `master`
gera um artefato com o pacote `.msix` assinado e as dependências.

Opcional: para abrir no Visual Studio 2022, instale a carga *Desenvolvimento para a Plataforma
Universal do Windows* e abra `xbox-app/xISOBurn.sln` (configuração `Release | x64`).

## Origem da técnica de acesso ao drive

A forma de abrir o drive vem da PR #11 do XBSX2,
["UWP/CDVD: Add support for the Xbox disc drive"](https://github.com/XboxEmulationHub/XBSX2/pull/11)
(arquivo `pcsx2/CDVD/Windows/IOCtlSrcXbox.cpp`). No console, `CreateFile("\\.\X:")` e os
`IOCTL_CDROM`/`IOCTL_DVD` são bloqueados. O caminho que funciona em Modo Dev é abrir a interface
CD-ROM como `CustomDevice` e enviar `IOCTL_SCSI_PASS_THROUGH`.
