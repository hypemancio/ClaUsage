# ClaUsage

Quanto hai usato dei tuoi abbonamenti AI, nella tray di Windows. Scritto in C#/WPF su .NET 10,
senza dipendenze esterne.

## Cosa fa

- **Icona tray** con la percentuale più alta tra tutti i limiti, colorata verde, arancio o rosso.
  In alternativa un anello-indicatore. Il tooltip mostra Session e Weekly di ogni account.
- **Pannello** che si apre cliccando l'icona o con **Ctrl+Alt+U**:
  - **All usage**: una scheda per account con barre, reset, e un segno dove sarebbe un ritmo uniforme.
    Sotto ogni barra: "On pace", "x% under pace", oppure "Runs out in 2h" se al ritmo attuale finisci
    prima del reset.
  - **Priority**: solo ciò che conta. Account da ri-autenticare, limiti esauriti, in esaurimento, o
    consumati troppo in fretta.
- **Notifiche** quando un limite supera la soglia "Running low" (70%) e "Almost used up" (90%).
- **Tasto destro su una scheda**: aggiorna, rinomina, rifai login, nascondi metriche, sposta, rimuovi.
- **Più account** per provider. Lo stesso login non viene aggiunto due volte.
- **Avvio con Windows**, intervallo di refresh, soglie: tutto da Settings.
- **Riga di comando**: `ClaUsage.exe --json` stampa l'ultimo stato letto dalla tray, senza chiamare i provider.

## Provider

| Provider | Login | Mostra |
|---|---|---|
| Claude | Browser (OAuth claude.ai, solo `user:profile`) | Session, Weekly, Sonnet, Opus, limiti per modello (es. Fable), Extra usage, piano |
| ChatGPT | Browser (OAuth OpenAI, porta 1455), fallback device code | Session, Weekly, Spark, Rate limit resets, Credits, Credit limit, piano |
| OpenRouter | API key (Management o standard) | Balance, Today, Yesterday, This week, This month, Prior 30 days, Key limit, Budget, Free requests |

Con una **Management key** (da openrouter.ai/settings/management-keys) vedi tutto l'account: saldo e spesa
sommata su tutte le chiavi. Con una **chiave standard** vedi solo la sua spesa e i suoi limiti. Giorni, budget
e limiti si azzerano a mezzanotte UTC, la settimana parte il lunedì. Se la chiave viene rifiutata, la scheda
offre **Replace key**.

Il token Claude ha scope `user:profile`: legge uso e profilo, non può eseguire modelli né creare chiavi.
ClaUsage non legge le credenziali di Claude Code, Codex o altri tool: ogni account fa il proprio login.

## Sicurezza e dati

Tutto in `%APPDATA%\ClaUsage`:

| File | Contenuto |
|---|---|
| `accounts.dat` | Account e token, cifrati con DPAPI per il tuo utente Windows |
| `settings.json` | Preferenze |
| `snapshots.json` | Ultimo uso letto, senza token (usato da `--json`) |
| `claUsage.log` | Errori |

## Limiti di richieste

Anthropic concede circa 30 letture di uso all'ora per account, condivise con Claude Code. Il refresh di
default è ogni 5 minuti. Su un 429 ClaUsage non invia nulla finché la finestra non scade, nemmeno su
refresh manuale. Il piano Claude viene riletto al massimo una volta l'ora.

## Build

Serve il .NET 10 SDK.

```powershell
.\build.ps1                 # dist\ClaUsage.exe piccolo, richiede .NET 10 Desktop Runtime
.\build.ps1 -SelfContained  # dist\standalone\ClaUsage.exe compresso (~75 MB), gira su qualsiasi Windows 10/11 x64
```

Opzioni da riga di comando:

```
ClaUsage                         Avvia la tray app
ClaUsage --background            Avvia senza aprire il pannello (usato dall'avvio automatico)
ClaUsage --json [claude|codex|openrouter]   Stampa l'ultimo uso letto, in JSON
ClaUsage --demo                  Dati di esempio, nessun login
ClaUsage --selftest              Verifica parser e callback OAuth
```

## Aggiornamenti automatici

L'app controlla le Release di `hypemancio/ClaUsage` 20 secondi dopo l'avvio e poi una volta al giorno.
Se trova una versione nuova manda una notifica, e nel pannello compare **Update**. L'aggiornamento:

1. scarica l'exe della stessa variante, normale o standalone;
2. lo confronta con il file `.sha256` della release e si ferma se non coincide;
3. sposta da parte l'exe in uso, mette al suo posto quello nuovo e lo riavvia;
4. al riavvio successivo cancella la copia vecchia.

Il controllo si disattiva da Settings, dove c'è anche **Check now**. Le build di sviluppo in `bin\` non si
aggiornano da sole. La repository deve essere **pubblica**: su una privata GitHub risponde 404 e l'app
considera di non avere aggiornamenti.

### Pubblicare una versione

```powershell
# 1. Aggiorna <Version> in src/ClaUsage/ClaUsage.csproj (es. 0.6.0) e fai commit
git tag v0.6.0
git push origin main --tags
```

GitHub Actions esegue il selftest, compila `ClaUsage.exe` e `ClaUsage-standalone.exe`, calcola gli
SHA-256 e crea la Release. La versione dell'exe viene presa dal tag.

## Loghi

I loghi di Claude, OpenAI e OpenRouter servono solo a indicare a quale servizio appartiene un account.
Sono marchi dei rispettivi proprietari. ClaUsage non è affiliato né approvato da loro.

## Struttura

```
src/ClaUsage/
  Core/       modelli, storage DPAPI, OAuth (PKCE + server loopback), servizio di refresh, dati demo
  Providers/  ClaudeProvider, CodexProvider
  UI/         pannello, finestre di login e settings, icona tray
```

Aggiungere un provider: implementare `IUsageProvider` e registrarlo in `Providers.All`.

## Crediti

by MANCIO | hypemancio | @_hypemancio_

Parte della lettura dei dati dai provider deriva da un progetto open source con licenza MIT.
Vedi `THIRD_PARTY_NOTICES.txt`.
