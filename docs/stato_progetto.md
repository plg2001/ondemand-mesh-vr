# Stato del progetto (11 novembre 2026)

Riassunto per riprendere il lavoro, per te e per chi apre una nuova conversazione con l'assistente. Descrive **dove
siamo, cosa è stato misurato, cosa non è stato verificato e cosa resta da fare**. Come è fatto il codice è in
`CLAUDE.md` (architettura) e `README.md` (uso; il Quest è nella sezione 8).

## 1. Obiettivo (dalle risposte della relatrice, Claudia)

Ricostruire oggetti 3D **on demand** per visori standalone (Meta Quest 2 / 3S):

- la scena 3D esiste già (mesh preparate a monte) e sul visore viene conservata come **nuvole di punti sparse**,
  una per oggetto (per ora: i punti stanno già sul visore; provare anche con i punti sull'Edge è "un esperimento
  forse utile", non richiesto);
- quando un oggetto **entra nel campo visivo**, il visore ne manda i punti a un nodo **Edge**, che ricostruisce la
  mesh con uno dei tre modelli generativi (MeshAnything V2, FastMesh, TreeMeshGPT) e la rimanda;
- i modelli ricostruiscono **l'oggetto intero**, non la parte visibile;
- gli oggetti fuori vista tornano a essere solo punti.

Termine usato da Claudia: **on demand** (non "streaming").

## 2. Fase 1: costo dei modelli (fatta)

Misure su questo PC (i5-9400, 8 GB RAM, GeForce GT 1030 da 2 GB), report in `benchmarks/results/final_*`, tabelle in
[risultati_benchmark.md](risultati_benchmark.md), analisi in [requisiti_calcolo.md](requisiti_calcolo.md).

- Il costo **non dipende dalla densità dei punti** (100, 500 o 2048: ±4%): dipende dal numero di facce generate.
- Generazioni **complete** disponibili: MeshAnything V2 sulla scatola (GPU fp16: 74 s; CPU: 274 s) e FastMesh sul
  pupazzetto (CPU: 225 s, 1112 vertici, 5192 triangoli). **TreeMeshGPT: solo run troncati a 64 token, mai una mesh
  completa.**
- Qualità (Chamfer-L2 ↓ / F-score@1% ↑): FastMesh pupazzetto 0,00041 / 0,47; MeshAnything scatola 0,0047 / 0,23.
  **Non confrontabili** tra loro: forme diverse. Solo nuvole pulite campionate da mesh esistenti.
- Stime (non misure) sul visore, fp16, mesh da 800 facce: Quest 2 da circa 27 s (FastMesh) a 219 s (MeshAnything
  V2); sull'Edge (stima per una RTX 4090) 2,6 / 8,8 / 18,4 s (FastMesh / TreeMeshGPT / MeshAnything V2).

## 3. Prototipo costruito (Fase 2)

| Parte | Dove | Cosa fa |
|---|---|---|
| Server Edge | `edge/`, `start_edge_server.bat` | FastAPI: riceve i punti (float32 grezzi), risponde con la mesh `.glb`; backend **mock** (involucro convesso, ritardo simulato) o **modello** vero; una richiesta alla volta |
| Scena di prova | `data/scenes/` | `build_scene.py` + `demo_room.yaml`: 6 oggetti, ognuno come nuvola da 2048 punti (144 KB in tutto, contro 2743 KB delle mesh originali) |
| App Unity | `unity/OnDemandMeshVR` | punti come segnaposto, richiesta all'Edge quando l'oggetto è inquadrato, rilascio dopo 3 s fuori vista, cache dei `.glb`, pannello di stato nel visore, log CSV dei tempi |
| Quest | `tools/quest/`, menu Unity **Tools → Quest** | build dell'APK, installazione, server via cavo o Wi-Fi, log dal visore, copia dei risultati |

Stato sul visore: l'app è stata **provata sul Quest 2**: l'analogico difettoso non muove più nulla
(`disableStickLocomotion`) e il comportamento è uguale a quello nel simulatore. Misure dal visore in
`benchmarks/results/quest/` (sezioni 4.1-4.4: prima prova, cavo contro Wi-Fi con gli fps, primo modello vero).

## 4. Misure fatte con il prototipo (sul PC, backend mock da 2 s)

- Rete locale sullo stesso PC: **circa 6-12 ms**. La **prima richiesta di ogni Play** costa da 1 a 2,7 s in più
  (avvio dell'editor, varia da prova a prova): va riportata a parte.
- La coda è seriale: la latenza percepita cresce di **circa 2 s per ogni oggetto davanti** (con 6 oggetti in vista
  l'ultimo compare dopo 7-9 s). Con un modello vero, più lento del mock, peggiora.
- Una mesh già in cache ricompare in **6-15 ms**, senza richiesta.
- Memoria: le nuvole da 2048 punti (24 KB l'una) risparmiano molto solo per oggetti con tante facce (coniglio 1222 KB,
  gatto 999 KB), poco o nulla per quelli semplici (scatola 0,2 KB). Una mesh generata da 800 triangoli pesa circa
  14 KB, **meno di una nuvola da 2048 punti**: il vantaggio è rispetto alle mesh originali dense, non a quelle
  generate. Da rimisurare con le mesh dei modelli veri e con meno punti (500 punti = 5,9 KB).

### 4.1 Prime misure sul Quest 2 (backend mock da 2 s)

Dati in `benchmarks/results/quest/20261111_112533/` (`edge_timings.csv`, `ondemand_events.csv` dal visore; righe
corrispondenti del log del server in `edge_requests.csv`; riassunto in `summary.md`): tre sessioni,
10 ricostruzioni dall'Edge e 29 mesh rimostrate dalla cache. Due sessioni brevi il 10 novembre (2 oggetti l'una) e
una completa l'11 novembre con i 6 oggetti della scena. **Non è registrato se il collegamento era via cavo o via
Wi-Fi.**

| Misura (sul visore) | Prima richiesta di ogni sessione | Richieste successive (7) |
|---|---|---|
| Rete (andata e ritorno − tempo del server) | 73-128 ms | mediana 57 ms (22-145 ms) |
| Caricamento del `.glb` (glTFast) | 56-67 ms | **circa 28 ms**, uguale da 180 a 4090 triangoli |
| Totale per oggetto (rete + server + caricamento) | 2,15-2,30 s | mediana 2,11 s |
| Mesh dalla cache | – | mediana 15,5 ms (max 21 ms, 29 eventi) |

- Rete: **5-10 volte il PC** (6-12 ms) e molto variabile; l'oggetto con la risposta più grande (sfera, 74 KB) non è
  sempre il più lento (145, 86 e 36 ms), quindi conta più la variabilità del collegamento che la dimensione.
- Caricamento: costa circa il doppio la prima volta (avvio di glTFast e degli shader), poi è costante; rispetto
  ai 2 s del mock è trascurabile. Anche la cache costa di più che sul PC (15 ms contro 6-15 ms).
- Coda: nella sessione con 6 oggetti tutti in vista dall'inizio le mesh compaiono dopo 2,5 / 3,7 / 4,3 / 9,0 /
  9,2 / 12,0 s (`visible_to_mesh_ms`): come nell'editor, circa 2,1 s in più per ogni oggetto davanti in coda.
- Il server ha registrato anche altre richieste (11:06 e 11:17) che non sono nei CSV del visore: probabilmente
  prove nell'editor.
- Orologi: durante queste prove **il PC era indietro di circa 14 s** (misurato con adb: +14,1 s; il visore coincide
  con l'ora di internet). Abbinando le richieste dell'app al log del server la stima è +14,0 s, coerente.
  L'orologio del PC è stato sincronizzato l'11 novembre alle 11:44: dopo, la differenza misurata è +0,05 s
  (entro la precisione della misura). Windows sincronizza l'ora solo ogni tanto (il servizio Ora di Windows parte
  su richiesta e poi si ferma, è normale), quindi la differenza può tornare: ogni sessione la misura all'inizio e
  alla fine, `benchmarks/quest_report.py` la corregge e avvisa se cambia durante la sessione.
- **Gli fps non sono nei CSV dell'app** (si vedono solo sul pannello). Il visore però scrive ogni secondo nel log
  di Android una riga `VrApi` con fps, frame scartati, tempo per frame, uso di CPU e GPU e temperatura: la
  registra `tools\quest\session_usb.bat` / `session_wifi.bat` (sezione 8). Le righe vengono anche da altri processi
  (il menu di sistema a 90 Hz) e vanno filtrate per processo; col visore non indossato non ce ne sono. Prime misure
  nella sezione 4.2.

### 4.2 Cavo contro Wi-Fi sul Quest 2 (11 novembre, backend mock da 2 s)

Una sessione per collegamento con `session_usb.bat` / `session_wifi.bat` (circa 40 s ciascuna, 6 oggetti, app
riavviata, orologi allineati a +0,06 s): `benchmarks/results/quest/20261111_115613_usb/` e `20261111_115405_wifi/`,
con `summary.md`. Il server conferma il collegamento: 6 richieste da `127.0.0.1` via cavo, 6 da `192.168.1.28` (il
visore) via Wi-Fi.

| Misura | Cavo USB | Wi-Fi |
|---|---|---|
| Rete, prima richiesta | 20 ms | 22 ms |
| Rete, altre 5 richieste | mediana 16 ms (14-27) | mediana **29 ms** (25-44) |
| Caricamento del `.glb` | 52 ms la prima, poi 28 ms | 59 ms la prima, poi 28 ms |
| Totale per oggetto (mediana) | 2,06 s | 2,06 s |
| Mesh dalla cache (mediana) | 15,4 ms | 15,4 ms |
| fps a regime: mediana (minimo) | 72 (54) | 72 (50) |
| Secondi sotto il 90% dei 72 fps | 1 su 36 | 2 su 34 |
| Tempo dell'app per frame (mediana) | 6,8 ms | 6,5 ms |

- **Il Wi-Fi aggiunge circa 13 ms a richiesta**: trascurabile rispetto ai 2 s del mock e ancora di più rispetto ai
  secondi dei modelli veri. Sono però solo 5 richieste per collegamento, in un solo posto e momento: nella
  sessione del mattino (sezione 4.1, collegamento non registrato) la rete era più lenta e più variabile (mediana
  57 ms, fino a 145 ms). Per la tesi servono più sessioni per collegamento.
- **fps**: a regime l'app sta a 72 fps (la frequenza del Quest 2), con circa 6,5 ms di lavoro per frame su 13,9 ms
  disponibili (uso della GPU circa 0,57). L'unico calo vero è **nei 2 s dopo la prima mesh** (54-70 fps via cavo,
  50-72 via Wi-Fi): uguale con i due collegamenti, quindi dipende dal primo caricamento con glTFast (shader e
  inizializzazione, lo stesso motivo per cui la prima mesh si carica in 52-59 ms invece di 28), non dalla rete. Le
  mesh successive, dall'Edge o dalla cache, non fanno scendere gli fps. Prima della prima mesh ci sono piccoli
  cali (62-67 fps) di causa non ancora chiara. I primi 2 s dell'app (1 fps, circa 55 frame scartati) sono il suo
  avvio e il riassunto li riporta a parte.
- I tempi dall'ingresso in vista alla mesh (ultimo oggetto dopo 7,4 s via cavo e dopo 10,0 s via Wi-Fi) dipendono
  da quando gli oggetti sono entrati nel campo visivo, cioè da come ci si è mossi: non servono a confrontare i
  collegamenti.

### 4.3 Server con un modello vero, sul PC (11 novembre, senza visore)

MeshAnything V2 sulla GT 1030 (fp16, attenzione SDPA), nuvole della scena di prova inviate come fa il visore (2048
punti float32, senza normali: le stima il server).

- **Avvio del server: 44 s** la prima volta (14 s di caricamento dei pesi, il resto import di Python), 16-17 s le
  volte dopo. La sessione aspetta il server fino a 120 s quando si usa un modello: basta.
- **Scatola (`box_01`): 70 s** sul server (68 s di generazione, 1440 token, 2,2 s per stimare le normali), 340
  triangoli, 6,8 KB. Ripetuta due volte con lo stesso risultato; la mesh coincide con i punti (distanza media 0,009
  su un oggetto lungo 1). Coerente con i 74 s della Fase 1, anche con Unity aperto.
- **Bacchetta (`wand_01`): non finisce in 5 minuti** (2971 token, poi il server risponde 504 per il limite
  `--time-budget-s 300`). Come nella Fase 1, su questa GPU MeshAnything completa solo forme semplici.
- Memoria della GPU: picco di 1905 MiB su 2048 (non è finita nella RAM di sistema, ma il margine è minimo con altri
  programmi aperti). RAM libera prima dell'avvio: 1,8 GB su 8.
- **Problema della coda trovato**: l'app serve sempre l'oggetto in vista più vicino e riprova quelli falliti dopo
  10 s. Nella scena il coniglio e il pupazzo (2,2 m) sono più vicini della scatola (2,6 m): con un modello vero
  fallirebbero dopo 5 minuti l'uno, verrebbero riprovati e la scatola non avrebbe mai il suo turno. Per la prova sul
  visore il server ha ora l'opzione `--objects box_01`, che rifiuta subito gli altri oggetti (HTTP 409, non
  registrati): restano punti senza bloccare la coda. La correzione vera va nell'app (sezione 6, punto 2).

### 4.4 Primo modello vero con il visore (11 novembre, Wi-Fi)

Sessione `benchmarks/results/quest/20261111_160430_wifi/` con
`--backend model --model meshanything --time-budget-s 300 --objects box_01`, guardando la scatola.

- **La scatola è comparsa sul Quest 66,9 s dopo la richiesta** (server 66,4 s di cui 64,9 s di generazione, rete
  22 ms, caricamento del `.glb` 60 ms): la stessa mesh da 340 triangoli ottenuta sul PC. La richiesta è partita 9 s
  dopo il riavvio dell'app.
- **Durante i 67 s di attesa l'app è rimasta sempre a 72 fps** (67 campioni, minimo 72): la richiesta in corso non
  pesa sul visore. Unico calo, come col mock, nel secondo dopo la mesh (61 fps).
- Gli altri oggetti sono rimasti punti; il pannello mostrava in rosso il rifiuto del server (`<oggetto>: HTTP 409 ...
  not served by this server`): è l'effetto atteso di `--objects`, non un errore.
- **Misura da correggere nell'app**: `visible_to_mesh_ms` vale 34,0 s, meno della richiesta. L'attesa riparte da
  zero se l'oggetto resta fuori vista per più di 3 s (`releaseAfterSeconds`), anche con la richiesta ancora in corso:
  durante l'attesa lo sguardo si è spostato e il conteggio è ripartito al rientro. Con il mock da 2 s non si notava.
  Per la tesi vale il tempo dalla richiesta alla mesh (`total_ms` in `edge_timings.csv`); l'app va corretta alla
  prossima build (sezione 6, punto 2).

## 5. Cosa NON è stato verificato

- Il **modello vero con il visore**: provato una volta, solo MeshAnything V2 e solo sulla scatola (sezione 4.4); gli
  altri modelli e le altre forme non finiscono in tempi utili su questa GPU.
- **Cavo contro Wi-Fi**: c'è una sola sessione per collegamento (5 richieste dopo la prima), con il backend mock, in un
  solo posto e momento (distanza dal router non annotata). Non provati: più sessioni, distanza, rete carica, mesh più grandi dei modelli veri.
- Il **tracciamento delle mani** (attivo nel progetto per Android).
- Le nuvole sono **pulite** (campionate da mesh esistenti, senza rumore né buchi, normali stimate dal server):
  l'obiettivo reale potrebbe avere scansioni peggiori.
- I materiali generati hanno bisogno di *Render Face = Both* (le facce prodotte non hanno un'orientazione coerente
  per Unity); i modelli producono solo geometria, **nessun colore o texture**.

## 6. Da fare (in ordine suggerito)

1. Cavo contro Wi-Fi: primo confronto fatto (sezione 4.2). Per numeri da tesi ripetere 3 sessioni per collegamento
   (`session_usb.bat` / `session_wifi.bat`), annotando distanza dal router e altri dispositivi.
   Opzionale, alla prossima build: precaricare glTFast all'avvio con una mesh minuscola per togliere il calo di fps
   alla prima mesh, e aggiungere ai CSV dell'app il collegamento e gli fps.
2. Ridurre la coda (serve una build): priorità all'oggetto al centro della vista; **non riprovare a ripetizione gli
   oggetti che hanno superato il limite di tempo** (oggi ogni 10 s, e se sono più vicini bloccano gli altri per sempre,
   sezione 4.3); **non far ripartire l'attesa misurata** (`visible_to_mesh_ms`) mentre la richiesta dell'oggetto è
   in corso (sezione 4.4); opzionale un backend "replay" che restituisce mesh già generate con ritardi realistici,
   per le dimostrazioni.
3. Modello vero con il visore: fatto per la scatola con MeshAnything V2 (sezione 4.4, comando nel README). Restano
   gli altri modelli, la mesh completa di TreeMeshGPT e soprattutto il PC del laboratorio, per i numeri sulla GPU
   potente con tutti gli oggetti.
4. Misurare memoria e qualità con le mesh dei modelli, variando il numero di punti.
5. Confronto **Edge contro inferenza sul Quest** (proposta dello studente, ancora senza risposta della relatrice):
   nel progetto c'è già il pacchetto Unity Inference Engine (`com.unity.ai.inference`).
6. Robustezza: nuvole con rumore e buchi, scena reale.
7. Scrittura della tesi.

**Domande ancora aperte per Claudia**: tempo di attesa accettabile e numero di oggetti per scena; cosa mostrare
nell'attesa (finora: i punti); come gestire colori e texture; confronto Edge contro Quest; accesso al PC del
laboratorio (offerto, "se non si riesce con il tuo").

## 7. Contesto pratico

- Windows 11, Python 3.11 (`.venv`), Unity 6.3 LTS (6000.3.26f1), progetto in `unity/OnDemandMeshVR`, repository
  `plg2001/ondemand-mesh-vr`, ramo `main`. Il disco è stato quasi pieno: tra pesi (10,7 GB), `.venv` (6 GB) e cache
  di Unity (4 GB e in crescita) il progetto supera i 20 GB; su GitHub ne finiscono circa 100 MB.
- Quest 2 con Android 14 (API 34). **Un controller ha lo stick difettoso (deriva)**: mai dipendere dall'analogico;
  si cammina e si gira fisicamente. Il gamepad PS4 è stato considerato e rimandato.
- Il file `unity/OnDemandMeshVR/ProjectSettings/UnityConnectSettings.asset` risulta modificato da Unity (servizi
  attivati) e **non è stato committato di proposito**.
- Documentazione e README in italiano, codice e commenti in inglese. Lo studente non conosceva Unity né i visori:
  spiegare i passaggi uno per uno, nominare ogni file e dire sempre cosa è **verificato** e cosa è solo previsto.

## 8. Comandi utili

```powershell
python -m pytest -q tests                      # 65 test, senza pesi né GPU
start_edge_server.bat                          # server mock per le prove nell'editor
tools\quest\check_headset.bat                  # il Quest è collegato e autorizzato?
tools\quest\server_usb.bat                     # server + collegamento via cavo (prima prova)
tools\quest\server_wifi.bat                    # server raggiungibile dal Wi-Fi
tools\quest\install_app.bat                    # installa Builds\OnDemandMeshVR.apk
tools\quest\pull_results.bat                   # copia i CSV dei tempi dal visore
tools\quest\session_usb.bat                    # sessione di misura via cavo (anche session_wifi.bat)
python -m benchmarks.quest_report <cartella>   # rigenera summary.md di una cartella in benchmarks/results/quest
python data\scenes\build_scene.py              # rigenera la scena di prova
```

In Unity: **Tools → Quest → Prepare and build APK** (la prima build è durata 35 minuti; le successive dovrebbero
essere più brevi, ma non è stato misurato).
