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

## 6. Resultados (preencher depois do teste)

_Anotações do teste no console vão aqui._
