# Guida: provare il prototipo sul Meta Quest 2

Guida passo passo, da visore spento a misure salvate sul PC. Le parti 1-4 si fanno **una volta sola**; dopo, ogni
prova è la parte 5 (via cavo) o la parte 6 (via Wi-Fi).

> I nomi delle voci di menu del visore e dell'app per telefono cambiano un po' con gli aggiornamenti di Meta: se una
> voce non corrisponde esattamente, cerca quella più simile.

**Cosa serve**
- il Quest 2 carico, con i due controller (batterie AA);
- un telefono con l'app **Meta Horizon** (ex "Meta Quest"/"Oculus") e un **account Meta**;
- un **cavo USB-C dati**: alcuni cavi di sola ricarica non trasmettono dati, e il PC non vedrebbe il visore;
- il PC e il visore sulla **stessa rete Wi-Fi** (non una rete "ospiti", che isola i dispositivi);
- circa un'ora per la prima volta, soprattutto per la prima build di Unity.

---

## 1. Accendere e preparare il visore

1. **Ricarica.** La porta USB-C è sul lato sinistro del visore. Una carica completa richiede circa 2 ore.
2. **Accensione.** Il pulsante di accensione è sul lato destro del visore: **tienilo premuto per circa 3 secondi**,
   finché la spia diventa bianca e dentro compare il logo. Per spegnere: tienilo premuto finché compare il menu, poi
   *Spegni*.
3. **Controller.** Se sono nuovi, togli la linguetta di plastica dal vano batterie. Il pulsante con il logo Meta
   (controller destro) apre il menu; **tenendolo premuto** ricentri la vista davanti a te.
4. **Indossarlo.** Regola le cinghie laterali e quella superiore. Le lenti hanno tre posizioni: spostale con le dita
   finché l'immagine è nitida. Se porti occhiali, usa il distanziatore in dotazione.
5. **Primo avvio.** Il visore chiede lingua, rete Wi-Fi (scegli **la stessa del PC**) e un aggiornamento software:
   lascialo finire. Poi chiede di collegarlo al telefono: apri l'app Meta Horizon e segui le istruzioni (il visore
   mostra un codice da inserire nell'app).
6. **Confine (Guardian).** Il visore chiede di disegnare l'area di gioco: per questi test basta la modalità
   **stazionaria** (si sta fermi e si gira solo la testa).

Se il visore era già stato usato da qualcun altro ed è legato al suo account, va reimpostato o usato con quell'account:
chiedi a chi te l'ha dato.

---

## 2. Attivare la modalità sviluppatore

Serve per installare app dal PC.

1. **Account sviluppatore.** Dal PC vai su <https://developers.meta.com/horizon>, accedi con lo stesso account Meta del
   visore e crea una **organizzazione** (un nome qualsiasi, per esempio il tuo). Meta può chiedere di verificare
   l'account (numero di telefono o carta): senza verifica la modalità sviluppatore non si attiva.
2. **App sul telefono.** Apri Meta Horizon → *Menu* → *Dispositivi* → scegli il visore → *Impostazioni del visore* →
   **Modalità sviluppatore** → attivala.
3. **Riavvia il visore** (spegni e riaccendi).

> Quando ho preparato questa guida il PC vedeva già il tuo visore collegato come *unauthorized*: probabilmente la
> modalità sviluppatore è già attiva e manca solo il passo 3 della parte successiva.

---

## 3. Collegare il visore al PC

1. Collega il visore al PC con il cavo USB-C dati, e **indossalo**.
2. Dentro il visore compare la richiesta **"Consentire il debug USB?"**: spunta **"Consenti sempre da questo
   computer"** e premi *Consenti*. Se non la vedi, cerca tra le notifiche, oppure stacca e riattacca il cavo.
   (Può comparire anche una richiesta di accesso ai file: per i nostri test non serve.)
3. Sul PC fai doppio clic su **`tools\quest\check_headset.bat`**. Se tutto va bene scrive qualcosa come:

   ```
   headset: Quest 2 (serial 1WMH...), Android API level 34
   Android version: OK (project needs 34)
   app com.DefaultCompany.VRTemplate: not installed yet (Build And Run from Unity)
   ```

   | Messaggio | Cosa fare |
   |---|---|
   | `Put on the headset and accept 'Allow USB debugging'` | indossa il visore e accetta la richiesta del punto 2 |
   | `No headset found` | cavo dati? visore acceso? modalità sviluppatore attiva? Se sì, installa i driver ADB di Meta (*Oculus ADB Drivers*, dal sito sviluppatori Meta) o **Meta Quest Developer Hub**, che li include |
   | `PROBLEM: the Unity project requires API level ...` | aggiorna il software del visore (Impostazioni → Sistema/Generale → Aggiornamento software), oppure in Unity usa **Tools → Quest → Prepare project** e ricostruisci (parte 4) |

---

## 4. Costruire l'app per il Quest (una volta sola, **senza visore**)

Un comando di menu di Unity prepara le impostazioni e costruisce l'app: **Tools → Quest → Prepare and build APK**
(il menu *Tools* sta nella barra in alto, tra *Jobs* e *Tutorials*).

1. Esci dalla modalità Play e salva la scena con `Ctrl+S`.
2. **Tools → Quest → Prepare and build APK**.
3. **Se la piattaforma non è ancora Android**, Unity lo dice e chiede il permesso: premi *Passa ad Android*. Unity
   riconverte le risorse (alcuni minuti, la barra in basso mostra l'avanzamento). **Quando ha finito, ripeti il
   punto 2**: il passaggio di piattaforma interrompe il comando, che non può proseguire da solo.
4. Il comando controlla le impostazioni e **corregge solo quelle che non vanno**, elencandole nella Console
   (`[QuestBuild] ...`):
   - *Minimum API Level* abbassato a Android 10 (API 29): il template VR chiede Android 14, e un Quest 2 con un
     sistema più vecchio rifiuterebbe di installare l'app;
   - *Internet Access* = *Require* (senza, l'app non potrebbe parlare con il server);
   - *Allow downloads over HTTP* = *Always allowed* (il server usa `http` sulla rete locale);
   - *Scripting Backend* = IL2CPP e architettura ARM64 (la CPU del Quest);
   - formato `.apk`, non `.aab` (quello è per Google Play);
   - l'*XR Device Simulator* viene etichettato **EditorOnly**, così non entra nella build, e la scena viene salvata.
5. Poi parte la build. **La prima è lenta: 20-30 minuti, a volte di più su questo PC.** La barra di avanzamento si
   ferma spesso, ed è normale; non chiudere Unity e, se puoi, chiudi gli altri programmi pesanti (browser). La prima
   volta Unity scarica anche dei componenti di Gradle: serve internet.
6. Alla fine una finestra mostra il percorso del file: **`unity\OnDemandMeshVR\Builds\OnDemandMeshVR.apk`**.

Altre voci dello stesso menu:

| Voce | Cosa fa |
|---|---|
| **Prepare project** | applica solo le impostazioni, senza costruire |
| **Prepare, build and run on headset** | come sopra, poi installa e avvia l'app sul visore collegato (serve il visore) |

**Se la build fallisce**, una finestra mostra i primi errori e la Console li riporta per intero: mandameli. Gli errori
più probabili sono di Gradle (rete assente durante il download) o di spazio su disco (la build ne usa alcuni GB).

**Controllo facoltativo prima della build.** *Edit → Project Settings → XR Plug-in Management → Project Validation*,
scheda Android: se compaiono errori rossi, premi *Fix All*. Nel progetto OpenXR e *Meta Quest Support* sono già
attivi per Android.

**Installare l'app sul visore, quando sarà carico:** doppio clic su **`tools\quest\install_app.bat`** (visore
collegato col cavo e autorizzato, parte 3). Non serve riaprire Unity.

---

## 5. Prova via cavo USB (la più semplice)

Il visore raggiunge il server attraverso il cavo: niente Wi-Fi e niente firewall. Serve a verificare che tutto
funzioni; i tempi di rete però non sono quelli del Wi-Fi.

1. Visore collegato e acceso. Sul PC: doppio clic su **`tools\quest\server_usb.bat`**. Attiva il "tunnel" sul cavo
   e avvia il server nella stessa finestra (backend mock, 2 secondi per oggetto). **Lascia la finestra aperta.**
2. Installa e avvia l'app: doppio clic su **`tools\quest\install_app.bat`** (la installa e la fa partire). Le volte
   dopo, se l'app è già installata, avviala dalla *Libreria* → filtro **Origini sconosciute** (*Unknown Sources*) →
   **OnDemandMeshVR**.
3. **Cosa vedrai**: i punti azzurri degli oggetti davanti a te e, in basso, un **pannello di stato** che segue lo
   sguardo:
   - `Edge OK http://127.0.0.1:8000 (mock/convex-hull)` → il server è raggiungibile;
   - `Objects 6 | in view 3 | meshes 1 | waiting 2 | asking: box_01` → cosa sta succedendo;
   - `Last: box_01: 2.31 s (round trip ..., network ... ms)` → l'ultima mesh arrivata;
   - nella prima riga, i **fps** (il Quest 2 punta a 72 o 90).
   Guardando un oggetto, dopo circa 2 secondi i punti diventano la sua mesh; girati per più di 3 secondi e torna
   indietro per vedere la cache.
4. **La Console del visore**: doppio clic su **`tools\quest\show_logs.bat`** mostra in diretta gli stessi messaggi che
   vedi nella Console di Unity (anche gli errori).
5. **Dopo la prova**: doppio clic su **`tools\quest\pull_results.bat`** copia `edge_timings.csv` e
   `ondemand_events.csv` dal visore in `benchmarks/results/quest/<data_ora>/`.

Se stacchi il cavo o riavvii il visore, il tunnel si perde: rilancia `server_usb.bat`.

---

## 6. Prova via Wi-Fi (la misura vera della rete)

**Si può preparare anche senza visore.** Chiudi le altre finestre del server, fai doppio clic su
`tools\quest\server_wifi.bat` e, dal **telefono collegato allo stesso Wi-Fi**, apri l'indirizzo che la finestra
stampa (per esempio `http://192.168.1.27:8000/health`). Se il telefono mostra `{"status":"ok", ...}`, rete e
firewall sono a posto e il visore raggiungerà il server allo stesso modo. Se la pagina non si carica, sistema il
firewall come al punto 3 qui sotto.

1. Chiudi la finestra del server della prova USB.
2. Con il visore **ancora collegato al cavo**, doppio clic su **`tools\quest\server_wifi.bat`**. Scrive sul visore
   l'indirizzo del PC (per esempio `http://192.168.1.27:8000`) e avvia il server raggiungibile dalla rete.
3. **Firewall di Windows**: la prima volta compare una finestra di Windows Defender. Consenti l'accesso alle **reti
   private**. Se la tua rete Wi-Fi è impostata come *pubblica*, il firewall blocca comunque il visore: in Windows
   *Impostazioni → Rete e Internet → Wi-Fi → (la tua rete)* → tipo di profilo **Rete privata**.
4. Ora puoi **staccare il cavo**. Chiudi e riapri l'app sul visore (il nuovo indirizzo si legge all'avvio).
5. Il pannello deve mostrare `Edge OK http://192.168.1.27:8000`. Per copiare i risultati serve di nuovo il cavo
   (`pull_results.bat`).

Per tornare alla modalità via cavo basta rilanciare `server_usb.bat`: rimette l'indirizzo predefinito. Non serve rifare
la build in nessuno dei due casi.

---

## 6bis. Sessione di misura (cavo contro Wi-Fi)

Le parti 5 e 6 servono a far funzionare l'app. Per le **misure della tesi** usa invece una sessione, che fa tutto da
sola e registra anche gli **fps** del visore (non serve rifare la build):

1. Chiudi eventuali finestre del server ancora aperte. Visore acceso, **collegato al cavo** (anche per il Wi-Fi: in
   quel caso il cavo porta solo il registro degli fps, le richieste passano dal Wi-Fi).
2. Doppio clic su **`tools\quest\session_usb.bat`** (oppure `session_wifi.bat`). La finestra imposta il
   collegamento, avvia il server, misura la differenza tra l'orologio del visore e quello del PC e **riavvia l'app** sul
   visore.
3. Indossa il visore: prima guarda la stanza con tutti gli oggetti in vista e aspetta che compaiano tutte le mesh,
   poi girati dall'altra parte per qualche secondo e torna a guardarli (così si misura anche la cache). Ogni 10 s la
   finestra scrive gli fps dell'app.
4. Togli il visore **solo dopo** aver premuto **Invio** nella finestra: con il visore in standby non si disegna nulla e
   non ci sono fps da registrare.
5. La finestra copia tutto in `benchmarks\results\quest\<data_ora>_usb\` (o `_wifi`): i due CSV dell'app, il log del
   server (`edge_requests.csv`, con l'indirizzo da cui arrivano le richieste: `127.0.0.1` via cavo, quello del visore
   via Wi-Fi), `fps.csv` (una riga al secondo), `session.json` e il riassunto **`summary.md`**.

Fai una sessione per ciascun collegamento, nelle stesse condizioni.

---

## 7. Cosa annotare per la tesi

- La **prima mesh** di ogni avvio è più lenta (avvio dell'app): va riportata a parte.
- Confronta **cavo e Wi-Fi**: la differenza nella colonna `network_ms` è il costo della rete senza fili.
- Nel visore, `load_ms` (caricamento del `.glb`) sarà più alto che sul PC: è la CPU del Quest.
- Gli **fps** (`fps.csv` e `summary.md` della sessione) con i punti e con le mesh visibili.
- Annota le condizioni: distanza dal router, altri dispositivi in rete, livello di batteria.
- L'orologio del PC può essere sbagliato di qualche secondo (Windows lo sincronizza solo ogni tanto; prima delle
  prime misure era indietro di 14 s): la sessione misura la differenza all'inizio e alla fine e il riassunto la
  corregge, quindi non è obbligatorio sistemarlo. Se lo sistemi (*Impostazioni → Data e ora → Sincronizza ora*),
  fallo **prima** di una sessione, non durante: se cambia a metà, `summary.md` lo segnala ed è meglio ripetere.

I file in `benchmarks/results/quest/` non sono esclusi da git: committali solo quando vuoi conservarli.

---

## 8. Problemi frequenti

| Sintomo | Causa probabile e soluzione |
|---|---|
| `check_headset.bat`: `No headset found` | cavo solo ricarica, modalità sviluppatore spenta o driver mancanti (parte 3) |
| `unauthorized` | accetta "Consentire il debug USB?" dentro il visore |
| `install_app.bat`: `APK not found` | costruisci prima l'app in Unity (parte 4) |
| Installazione fallita con `INSTALL_FAILED_OLDER_SDK` | Android del visore più vecchio del *Minimum API Level* dell'app: aggiorna il visore, oppure **Tools → Quest → Prepare project** e ricostruisci |
| L'app parte ma resta nera, o non segue la testa | controlla OpenXR e *Project Validation* (parte 4); verifica che l'XR Device Simulator sia `EditorOnly` |
| Pannello: `Edge NOT REACHABLE` | la finestra del server è aperta? Via cavo: rilancia `server_usb.bat`. Via Wi-Fi: stessa rete, rete *privata*, firewall (parte 6) |
| Nei log: `Cleartext HTTP traffic not permitted` | *Allow downloads over HTTP* non è *Always allowed*: correggi e rifai la build |
| Nei log: `Insecure connection not allowed` | stessa causa del punto precedente |
| Punti rosa/magenta o invisibili | il materiale dei punti non è finito nella build: mandami il log |
| Non trovo l'app nel visore | *Libreria* → filtro *Origini sconosciute* |
| Nell'app vengo spinto indietro da solo / l'analogico non funziona | lo stick "deriva" (non torna al centro): vedi la parte 9 |

Se qualcosa non torna, la cosa più utile da mandarmi è l'output di `check_headset.bat` e quello di `show_logs.bat`.

---

## 9. Se uno stick del controller non funziona (deriva)

Sintomo tipico: nell'app ti muovi da solo, di solito all'indietro, anche con le mani ferme. Lo stick non torna al centro
e continua a "premere" in una direzione. Capita spesso sui controller del Quest 2 con l'uso.

**Cosa fa l'app.** Sul visore **spegne lo spostamento con gli stick** (camminata, rotazione a scatti e teletrasporto del
template): la camera si muove solo con la tua testa e con i tuoi passi. È anche il modo giusto di fare la prova della
tesi, camminare e girare attorno agli oggetti. Il pannello di stato lo conferma: nella prima riga compare
`sticks off`. Nel componente *Point Cloud Scene* la casella **Disable Stick Locomotion** (spuntata di default) lo
regola; **Disable Stick Locomotion In Editor** serve per provarlo anche nel simulatore. L'effetto si vede dalla
build successiva: serve ricostruire e reinstallare l'app (parte 4 e 5).

**Subito, senza ricostruire nulla** (con l'app già installata):
1. **Togli la pila dal controller difettoso.** Il Quest 2 usa una pila AA. Senza pila il controller è spento e non
   può inviare niente.
2. Per muoverti nei menu del visore usa **l'altro controller**, oppure il **tracciamento delle mani**: sul visore,
   *Impostazioni → Movimenti e tracciamento → Tracciamento di mani e corpo* → attivalo, e lascia attivo il passaggio
   automatico tra mani e controller (i nomi esatti possono cambiare con gli aggiornamenti). Con le mani si seleziona
   unendo pollice e indice.
3. Nell'app, il tracciamento delle mani è già attivo nel progetto.

**Per camminare serve spazio.** Con il confine in modalità **stazionaria** puoi solo girare la testa; per avvicinarti
agli oggetti servono almeno 2 × 2 metri liberi, con il confine a **scala della stanza** (*room-scale*). La stanza di
prova ha gli oggetti tra 2 e 4 metri davanti a te.

**Riparare lo stick.** In ordine di fatica: aria compressa o un po' di alcol isopropilico intorno allo stick, ruotandolo
per qualche minuto; sostituire il modulo dello stick (un componente economico, ma serve saldare); oppure sostituire il
controller, o chiedere assistenza a Meta se è ancora in garanzia. Non so in che stato di garanzia sia il tuo visore.

**E il controller della PS4?** Il Quest sa accoppiarsi a un gamepad Bluetooth, e un giorno si potrebbe far muovere la
camera con quello. Per ora non serve: l'app non richiede stick e, per la tesi, camminare davvero è più fedele. Se in
futuro ti mancasse lo spazio per camminare, si può aggiungere.

