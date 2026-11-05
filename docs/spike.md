# Marco 1: teste de viabilidade (spike)

O objetivo é descobrir, no console real, se o drive do Xbox Series X lê o DVD e **o que** ele entrega.
O app só lê. Não grava nada no disco nem no drive e ainda não usa a rede.

## 1. Compilar (GitHub Actions)

Não é preciso instalar nada no PC.

1. Faça push para `master` ou rode o workflow à mão: aba **Actions** → **build** → **Run workflow**.
2. Espere o job **App Xbox (UWP x64)** terminar. Ele leva de 5 a 10 minutos, porque a compilação .NET Native é lenta.
3. Na página da execução, baixe o artefato **`xISOBurn-xbox-0.1.N.0`** e extraia o zip. Dentro dele:
   ```
   xISOBurn.cer                                   (certificado público usado na assinatura)
   pacote\xISOBurn_0.1.N.0_Test\
       xISOBurn_0.1.N.0_x64.msix                  (o app)
       Dependencies\x64\*.appx                    (runtime .NET Native e VCLibs)
   ```

## 2. Instalar no Xbox (Device Portal, pelo navegador do PC)

Requisitos: Xbox em Modo Desenvolvedor e Device Portal ligado. Para ligar o Device Portal, abra o
**Dev Home** no console, vá em *Remote Access Settings*, marque *Enable Xbox Device Portal* e
defina usuário e senha. O endereço aparece no Dev Home.

1. No PC, abra `https://<ip-do-xbox>:11443`. Aceite o aviso de certificado do navegador e entre com o usuário e a senha.
2. Em **Home** → **My games & apps**, clique em **Add**.
3. Escolha o `xISOBurn_0.1.N.0_x64.msix` e clique em **Next**.
4. Em *dependências*, adicione **todos** os arquivos de `Dependencies\x64\` e clique em **Start**.
5. O app aparece na lista do Dev Home ("xISOBurn").

> O Xbox em Modo Dev **não** deveria exigir a instalação do certificado `.cer`: o pacote só precisa
> estar assinado. **Ponto a confirmar**: se a instalação reclamar da assinatura, anote a mensagem exata.
>
> Para instalar uma versão nova: como cada build gera um certificado diferente (enquanto não houver
> secret configurado), talvez seja preciso **remover** a versão antiga antes de instalar a nova.

## 3. Rodar o teste

1. Coloque o DVD (Acquaria) no drive do Xbox. Se o console abrir o player de filmes, feche o player.
2. Abra o xISOBurn e aperte **A** em "Executar teste". Não saia do app enquanto ele roda.
   O teste leva menos de 1 minuto se o disco estiver bom e pode levar vários minutos se houver
   setores ruins (cada um pode levar até 30 s).
3. No fim aparece um **RESUMO**. O log completo fica na LocalState.

## 4. Pegar o log

No Device Portal: **File explorer** → **LocalAppData** → `xISOBurn_<id>` → **LocalState**. Baixe:

- `spike-AAAAMMDD-HHMMSS.log`: o log completo;
- `spike-primeiros-1000-setores.bin`: os primeiros ~2 MB do disco, com os setores com falha preenchidos com zeros.

Me mande o log (ou pelo menos o RESUMO).

## 5. O que o teste responde (pontos que só o console pode verificar)

| # | Pergunta | Onde ver no log |
|---|----------|-----------------|
| 1 | A enumeração acha a interface CD-ROM e o `CustomDevice` abre em Modo Dev com só as duas `DeviceCapability` da PR? | "Procurando o drive", "Abrindo o drive" |
| 2 | O código IOCTL é `0x0004D004`? | "Código IOCTL" |
| 3 | Fabricante, modelo e firmware do drive | INQUIRY |
| 4 | Tipo de mídia (DVD-ROM/CD/BD) | GET CONFIGURATION |
| 5 | Capacidade (READ CAPACITY) é igual ao tamanho calculado pela estrutura do DVD? É **dupla camada**? OTP ou PTP? | READ CAPACITY, READ DVD STRUCTURE, "Comparação de tamanhos" |
| 6 | O disco tem CSS? De que regiões é? | READ DVD STRUCTURE formato 1 |
| 7 | Qual a região do drive do Xbox? | REPORT KEY formato 8 |
| 8 | Quantos setores por comando funcionam (16/31/32/48/64)? Os dados conferem? | "Teste de tamanho de transferência" |
| 9 | Os primeiros 1000 setores leem? É ISO9660/UDF? | "Leitura dos primeiros", "Sistema de arquivos" |
| 10 | **O drive entrega os setores de vídeo sem autenticação CSS?** Ele recusa (sense `05/6F/03`) ou entrega embaralhado? | "Leituras de amostra" e o RESUMO |
| 11 | A camada 1 e a passagem entre camadas leem? | amostras "fim da camada 0" / "início da camada 1" |
| 12 | Onde aparecem erros de mídia (`03/11/00` etc.) e quanto tempo cada um demora? | linhas com "falhou" |

### Como interpretar o item 10 (o mais importante)

- **Amostras leem e "embaralhados: 0"**: o disco não tem CSS nessas regiões, ou o drive autenticou
  sozinho. A ISO vai sair utilizável direto.
- **Amostras leem com "embaralhados" > 0**: o drive entrega os dados criptografados. A ISO vai sair
  embaralhada e só será útil com descriptografia posterior no PC (ex.: MakeMKV). É o comportamento
  previsto no projeto.
- **Amostras recusadas com `05/6F/03`**: o drive exige a autenticação CSS antes de entregar os setores
  de vídeo. Sem isso, o vídeo inteiro sairia como "setor ruim". Antes do Marco 2 precisamos decidir
  juntos o que fazer.

## 6. Resultados (05/11/2026, Xbox Series X, build 0.1.2.0, DVD "ACQUARIA")

Os logs completos estão em `spike-20261105-121317.log` (execução completa) e `spike-20261105-121301.log`
(execução interrompida no meio das amostras).

| # | Resultado |
|---|-----------|
| 1 | **Funciona.** Uma interface, `\\?\SCSI#CdRom&Ven_PLDS&Prod_DG-6M5S#...`, nome "XBOX". O `CustomDevice` abre com só as duas `DeviceCapability` da PR. |
| 2 | Código IOCTL `0x0004D004`, como esperado. |
| 3 | Drive **PLDS DG-6M5S**, firmware **014R**. |
| 4 | Perfil `0x0010` (DVD-ROM). |
| 5 | **DVD-9: 2 camadas, OTP.** Setor físico inicial `0x030000`, final `0xF635C6`, fim da camada 0 `0x1FB52F`. Camada 0 = 1.881.392 setores, camada 1 = 1.436.407 setores, **total 3.317.799 setores (6.794.852.352 bytes)**. O total bate exatamente com o tamanho do volume ISO9660. **READ CAPACITY devolve 15.939.015**, que é `final − inicial + 1` ignorando o OTP: o valor está **errado** para esse disco. Pedir a estrutura da camada 1 devolve `05/24/00` (normal em OTP: só a camada 0 tem descritor). |
| 6 | **Sem proteção (CPST = 0, sem CSS).** Regiões do disco: 1 e 4. |
| 7 | Drive na região 1 (RPC Phase II, 4 trocas restantes). Isso não importa para este disco, porque sem CSS o drive não aplica região. |
| 8 | 16, 31, 32, 48 e **64** setores por comando funcionam, e o conteúdo confere com leituras de 16. |
| 9 | Os setores 0–999 leem sem erro. A estrutura é ISO9660 ("ACQUARIA") + UDF (BEA01/NSR02/TEA01 e AVDP no setor 256), o padrão de DVD-Video. O primeiro VOB está no LBA 328. |
| 10 | Nenhum setor embaralhado e nenhuma recusa: o disco não tem CSS, então a ISO sai utilizável direto. |
| 11 | A leitura atravessa a troca de camada: o fim da camada 0 (LBA 1.881.376) lê dados MPEG, e o início da camada 1 (LBA 1.881.392) lê com sucesso. **Ponto a verificar:** os 16 primeiros setores da camada 1 voltaram todos zerados em 0,7 ms. Pode ser preenchimento normal entre arquivos ou um comportamento do drive. A leitura completa (Marco 2) vai mostrar se os setores seguintes têm vídeo. |
| 12 | Nenhum erro de mídia nos trechos lidos. As amostras de 25%, 50%, 75% e "fim do disco" usaram a capacidade errada e caíram **além do fim real** (LBAs > 3.317.799). Elas falharam com "resposta curta (3 bytes)", sem sense, levando cerca de 1,1 a 1,5 s cada. |

### Consequências para o Marco 2

- **O tamanho do disco vem da estrutura do DVD** (cálculo OTP), conferida com o ISO9660. READ CAPACITY fica só como último recurso (CD/BD).
- **Erros podem vir sem sense.** A falha fora do fim do disco veio como resposta de 3 bytes, sem dados de sense. A leitura precisa tratar isso como falha comum. Também vale tentar REQUEST SENSE em seguida para descobrir o motivo.
- **Bloco padrão de 32 setores (64 KiB)**, configurável até 64.
- **Velocidade:** os números do spike (1–2,5 MiB/s) vêm de leituras curtas, ainda com o disco acelerando, e não servem como estimativa. O Marco 2 vai medir a velocidade sustentada.
- **Sair do app no meio:** a primeira execução foi interrompida sem erro no log (provavelmente o app foi fechado ou suspenso). O Marco 2 vai tratar o botão B e avisar antes de sair.
