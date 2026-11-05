# Marco 2: leitura completa com tratamento de erros (arquivo local)

O app lê o disco inteiro com novas tentativas por bloco e por setor. Os setores que não lerem viram
zeros e entram num mapa. O resultado vai para a **LocalState** do app, no armazenamento interno do
Xbox. Ainda não há rede: isso fica para o Marco 3.

## Como a leitura funciona

1. **Tamanho do disco:** vem da estrutura do DVD (cálculo OTP) e é conferido com o volume ISO9660.
   O READ CAPACITY do drive do Xbox dá um valor errado em DVD-9 OTP (veja docs/spike.md).
2. **Leitura em blocos** de `SectorsPerRead` setores (padrão 32 = 64 KiB) com READ(10).
3. Se um bloco falhar, ele é lido de novo **`BlockRetries`** vezes (padrão 5).
4. Se continuar falhando, a leitura passa a ser **setor a setor**, com 1 + **`SectorRetries`** tentativas cada (padrão 5).
5. Setor que não ler é **preenchido com zeros** e anotado no mapa. A cópia **nunca para** por causa de
   setor ruim. Ela só para se o disco sair do drive (`02/3A`), se você cancelar ou se houver erro de gravação.
6. O **SHA-256** é calculado durante a leitura, sobre exatamente os bytes gravados (com os zeros).
7. Se a falha vier sem sense, o app pede REQUEST SENSE para tentar descobrir o motivo.

## Arquivos gerados (LocalState)

| Arquivo | Conteúdo |
|---------|----------|
| `ACQUARIA.iso` | Imagem bruta, 2048 bytes por setor (só se `WriteImage` = true) |
| `ACQUARIA.map` | Setores **não lidos** (preenchidos com zeros), em intervalos `início-fim (quantidade) erro`, mais o SHA-256 |
| `ACQUARIA.sha256` | Hash no formato `sha256sum` (só quando a cópia termina) |
| `xisoburn-AAAAMMDD-HHMMSS.log` | Log da sessão: cada tentativa que falhou, com o sense, e o progresso a cada 5% |
| `config.json` | Configuração (criado na primeira execução) |

Quando a cópia é parcial (`StartSector`/`SectorLimit`), o nome ganha o sufixo `_lbaINICIO-FIM`. Se a
cópia for cancelada, a imagem é cortada no ponto em que parou, e o `.map` diz "INTERROMPIDO".

## config.json

```json
{
  "SectorsPerRead": 32,
  "BlockRetries": 5,
  "SectorRetries": 5,
  "TimeoutSeconds": 20,
  "StartSector": 0,
  "SectorLimit": 0,
  "WriteImage": true,
  "OutputName": ""
}
```

- `SectorsPerRead`: 1 a 64 (o spike confirmou até 64).
- `TimeoutSeconds`: 5 a 120. Esse é o tempo de cada comando, e um setor ruim pode levar vários segundos por tentativa.
  Pior caso de um bloco de 32 setores todos ruins: 6 + 32 × 6 = 198 comandos.
- `SectorLimit`: 0 = até o fim. Use para testar um trecho, por exemplo `200000` (cerca de 390 MiB).
- `WriteImage`: `false` lê o disco inteiro, gera o mapa e o hash, **sem gravar a imagem**. Serve para
  descobrir se o drive do Xbox lê o disco todo antes de ocupar 6,3 GiB.

Para editar sem teclado no console: no Device Portal, abra **File explorer** → LocalAppData →
`xISOBurn_...` → **LocalState**. Baixe o `config.json`, edite no PC e envie de volta com **Upload**.
O arquivo é relido a cada cópia.

## Roteiro de teste sugerido

1. **Instalar a versão nova.** Remova a anterior se o Device Portal reclamar da assinatura.
2. Abra o app e use **Detectar disco**. O painel "Disco" deve mostrar DVD-ROM, 2 camadas (OTP),
   **3.317.799 setores (6,33 GiB)**, volume ACQUARIA e troca de camada no LBA 1.881.392.
3. **Teste curto:** com `SectorLimit: 50000`, use **Copiar para LocalState**. Confira se a velocidade
   e o tempo restante fazem sentido. Teste o **B** para cancelar no meio.
4. **Leitura completa sem gravar** (`SectorLimit: 0`, `WriteImage: false`). Esse é o teste principal:
   ele mostra se o drive do Xbox lê os setores em que o leitor do PC falha. Me mande o `.map` e o log.
5. Se o passo 4 for bem, rode a **cópia completa com gravação** (`WriteImage: true`). O app reserva
   6,3 GiB no começo. Se faltar espaço, ele falha logo no início, e o erro aparece no log.

### Pontos a verificar no console

- **Velocidade sustentada** do drive (o spike não mediu isso direito).
- **Quanto demora** cada setor ruim e se as novas tentativas recuperam algum (o log mostra "lido na tentativa N").
- Se os erros vêm com sense (`03/11/00`...) ou como "resposta curta", e se o REQUEST SENSE ajuda.
- **Início da camada 1** (LBA 1.881.392): no spike, os primeiros 16 setores dali vieram zerados.
  Na leitura completa, veja se o log ou o mapa mostram algo de estranho nessa região.
- **Espaço:** se a LocalState aceita um arquivo de 6,3 GiB no armazenamento interno.
- **Suspensão:** o que acontece se o console apagar a tela ou se você apertar o botão Xbox durante a cópia.
