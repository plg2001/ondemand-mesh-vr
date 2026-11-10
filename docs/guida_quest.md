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
   | `PROBLEM: the Unity project requires API level ...` | aggiorna il software del visore (Impostazioni → Sistema/Generale → Aggiornamento software); se non basta, in Unity abbassa *Minimum API Level* (parte 4, punto 6) |

---

## 4. Preparare Unity per il Quest (una volta sola)

1. **XR Device Simulator solo nell'editor.** Nella Hierarchy seleziona *XR Device Simulator*; in cima all'Inspector,
   nel menu **Tag**, scegli **EditorOnly**. Gli oggetti con questa etichetta non finiscono nella build: sul visore il
   simulatore disturberebbe il tracciamento vero. (Il codice lo disattiva comunque, ma l'etichetta è la soluzione
   pulita.)
2. **Salva la scena**: `Ctrl+S`.
3. **Passa ad Android**: *File → Build Profiles* → seleziona **Android** → **Switch Platform**. Ci mette diversi
   minuti (Unity riconverte le risorse).
4. **XR per Android**: *Edit → Project Settings → XR Plug-in Management* → scheda con l'icona di Android → deve essere
   spuntato **OpenXR**; sotto *OpenXR*, sempre nella scheda Android, deve essere attivo **Meta Quest Support**. Nel
   progetto lo sono già: è solo un controllo.
5. **Project Validation**: *XR Plug-in Management → Project Validation*, scheda Android → **Fix All**. Ripeti finché
   non restano errori (gli avvisi gialli si possono lasciare).
6. **Player**: *Project Settings → Player* → scheda Android → *Other Settings*:
   - **Internet Access** = *Require* (senza, l'app non può usare la rete);
   - *Allow downloads over HTTP* = *Always allowed* (già impostato);
   - *Minimum API Level*: lascialo com'è, salvo il caso `PROBLEM` della parte 3.
7. **Prima build**: *File → Build Profiles* → in *Run Device* scegli il **Quest 2** (se non compare, premi *Refresh*
   con il visore collegato e autorizzato) → **Build And Run**. Quando chiede dove salvare l'`.apk`, crea la cartella
   `unity/OnDemandMeshVR/Builds` (è esclusa da git) e chiamalo `OnDemandMeshVR.apk`.
   **La prima build è lenta** (anche 20-30 minuti su questo PC); le successive molto meno. Alla fine Unity installa e
   avvia l'app sul visore da solo.

---

## 5. Prova via cavo USB (la più semplice)

Il visore raggiunge il server attraverso il cavo: niente Wi-Fi e niente firewall. Serve a verificare che tutto
funzioni; i tempi di rete però non sono quelli del Wi-Fi.

1. Visore collegato e acceso. Sul PC: doppio clic su **`tools\quest\server_usb.bat`**. Attiva il "tunnel" sul cavo
   e avvia il server nella stessa finestra (backend mock, 2 secondi per oggetto). **Lascia la finestra aperta.**
2. Avvia l'app sul visore: dopo *Build And Run* parte da sola; altrimenti *Libreria* → filtro **Origini sconosciute**
   (*Unknown Sources*) → **OnDemandMeshVR**.
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

## 7. Cosa annotare per la tesi

- La **prima mesh** di ogni avvio è più lenta (avvio dell'app): va riportata a parte.
- Confronta **cavo e Wi-Fi**: la differenza nella colonna `network_ms` è il costo della rete senza fili.
- Nel visore, `load_ms` (caricamento del `.glb`) sarà più alto che sul PC: è la CPU del Quest.
- I **fps** del pannello con i punti e con le mesh visibili.
- Annota le condizioni: distanza dal router, altri dispositivi in rete, livello di batteria.

I file in `benchmarks/results/quest/` non sono esclusi da git: committali solo quando vuoi conservarli.

---

## 8. Problemi frequenti

| Sintomo | Causa probabile e soluzione |
|---|---|
| `check_headset.bat`: `No headset found` | cavo solo ricarica, modalità sviluppatore spenta o driver mancanti (parte 3) |
| `unauthorized` | accetta "Consentire il debug USB?" dentro il visore |
| *Build And Run* non trova il visore | visore non autorizzato, oppure premi *Refresh* in *Run Device* |
| Installazione fallita con `INSTALL_FAILED_OLDER_SDK` | Android del visore troppo vecchio: aggiorna il visore o abbassa *Minimum API Level* |
| L'app parte ma resta nera, o non segue la testa | controlla OpenXR e *Project Validation* (parte 4); verifica che l'XR Device Simulator sia `EditorOnly` |
| Pannello: `Edge NOT REACHABLE` | la finestra del server è aperta? Via cavo: rilancia `server_usb.bat`. Via Wi-Fi: stessa rete, rete *privata*, firewall (parte 6) |
| Nei log: `Cleartext HTTP traffic not permitted` | *Allow downloads over HTTP* non è *Always allowed*: correggi e rifai la build |
| Nei log: `Insecure connection not allowed` | stessa causa del punto precedente |
| Punti rosa/magenta o invisibili | il materiale dei punti non è finito nella build: mandami il log |
| Non trovo l'app nel visore | *Libreria* → filtro *Origini sconosciute* |

Se qualcosa non torna, la cosa più utile da mandarmi è l'output di `check_headset.bat` e quello di `show_logs.bat`.
