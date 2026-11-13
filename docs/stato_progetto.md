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

### 4.5 App corretta sul visore (12 novembre, Wi-Fi)

Nuova build con le correzioni della sezione 6, punto 2. Due sessioni:

- **Mock** (`20261112_104605_wifi`, 6 oggetti): tutto funziona come prima (6 mesh, cache 15 ms, rete mediana 42 ms).
  **La prima mesh ora si carica in 28 ms**, come le altre (prima 52-60 ms): la preparazione all'avvio
  (`WarmUpMesh`) toglie il costo del primo caricamento di glTFast. **Il calo di fps dopo la prima mesh invece resta**
  (mediana 62, minimo 51 nei 2 s dopo): non dipende da glTFast. Ipotesi da verificare: il passaggio delle ombre
  (la mesh minuscola della preparazione forse non viene disegnata nella mappa delle ombre) o lo spegnimento dei punti.
- **MeshAnything V2 senza `--objects`, limite 120 s** (`20261112_104734_wifi`, interrotta: la finestra è stata chiusa
  senza Invio, quindi mancano i CSV dell'app e il riassunto; restano il log del server e gli fps). Il primo oggetto
  chiesto è stato il gatto: il server ha rinunciato dopo 121,7 s (504) e l'app, dal suo log, lo ha segnato
  `TooSlow, not retried` ed è **passata all'oggetto successivo** invece di richiederlo all'infinito. È la correzione
  della coda verificata sul visore. Non verificati: l'ordine "centro della vista" misurato oggetto per oggetto e
  l'attesa che non riparte durante una richiesta lunga (serve una mesh che arrivi mentre si guarda altrove).
- Tenere il visore a lungo dà nausea: le prove vanno tenute brevi (al massimo 1-2 minuti), con il mock o con
  `--objects` sulla scatola.
- **Timer, etichette e accuratezza** (`20261112_113450_wifi`, MeshAnything V2, `--time-budget-s 120 --objects
  box_01`, scena spostata): la scatola è arrivata **66,1 s** dopo la richiesta (server 66,0 s, rete 147 ms,
  caricamento 28 ms), F-score **0,70** (precisione 0,60, richiamo 0,85, Chamfer-L2 0,00101: la stessa mesh e la stessa
  accuratezza del giorno prima, la generazione è deterministica); il calcolo dell'accuratezza ha preso 91 ms. Il tempo
  "dall'ingresso in vista alla mesh" ora vale 66,1 s, uguale alla richiesta (prima 34 s per la stessa attesa). Gli altri
  oggetti, rifiutati da `--objects`, sono stati segnati `Refused, not retried` in pochi millisecondi senza ritardare la
  scatola; la mesh è poi ricomparsa dalla cache in 15 ms. Preparazione all'avvio: 76 ms. Il calo di fps dopo la prima
  mesh resta (minimo 37, mediana 54 nei 2 s dopo): ancora da capire (ombre? creazione dell'etichetta?).
- **Scena spostata (12 novembre)**, perché con il nuovo ordine conta l'angolo dallo sguardo e non la distanza:
  la scatola, la più veloce da ricostruire, ora è davanti a 1,8 m e all'altezza di un tavolo (0,8 m); il pupazzo è
  in fondo (4,9 m invece di 2,2); la bacchetta è spostata di lato, prima era dietro la scatola sulla stessa linea.
  Dall'altezza degli occhi la scatola è ora il primo oggetto richiesto, sia guardando dritto sia verso il basso (a
  3-17° dal centro della vista; prima veniva sempre la bacchetta, a 5-15°). Le sessioni fino all'11 novembre usano
  la disposizione precedente: i tempi dall'ingresso in vista alla mesh non sono confrontabili oggetto per oggetto.

### 4.6 Metodi classici contro modelli generativi (13 novembre, solo PC)

La domanda: **serve davvero un modello generativo?** I metodi classici di ricostruzione di superfici sono stati
misurati sugli **stessi ingressi** dei generativi (i file `.ply` da 2048 punti della Fase 1), con lo stesso riferimento
(nuvola densa della mesh originale) e la stessa misura (`quality_metrics`): `python -m benchmarks.classical_baselines`,
risultati in `benchmarks/results/classical_20261113_110702/` (tabelle complete in `summary.md`). Metodi
(`models/mesh_wrappers/classical.py`, Open3D): involucro convesso (= il mock), alpha shape, Ball Pivoting (BPA),
Poisson; i parametri dipendono solo dalla spaziatura dei punti, scelti guardando solo il coniglio e poi applicati a
tutte le forme. BPA e Poisson usano le normali: *vere* (della mesh) o *stimate* dai punti, come succede col visore.

| Forma | Involucro | Alpha | BPA (stimate) | Poisson (stimate) | BPA (vere) | Poisson (vere) | Miglior generativo |
|---|---|---|---|---|---|---|---|
| scatola | 0,99 | 0,52 | 0,97 | 0,91 | 0,97 | 0,94 | 0,23 (MeshAnything, 74 s GPU) |
| coniglio | 0,39 | 0,88 | 0,95 | 0,88 | 0,97 | 0,97 | nessuna mesh completa |
| pupazzo | 0,27 | 0,73 | 0,87 | 0,72 | 0,88 | 0,89 | 0,47 (FastMesh, 225 s CPU) |
| sfera | 1,00 | 0,20 | 1,00 | 0,98 | 1,00 | 0,98 | nessuna mesh completa |
| gatto | 0,17 | 0,86 | 0,92 | 0,71 | 0,95 | 0,97 | nessuna mesh completa |
| bacchetta | 0,32 | 0,82 | 0,88 | 0,82 | 0,97 | 0,97 | nessuna mesh completa |

- **Ball Pivoting con le normali stimate** (il caso realistico del visore) fa F-score 0,87-1,00 su tutte e sei le
  forme in **40-90 ms** sulla CPU di questo PC (stima delle normali ~70 ms a parte), con 2800-4100 triangoli; ridotto a
  1000 triangoli (leggero come le mesh dei generativi) resta a 0,83-1,00 in 50-180 ms. Poisson fa 0,94-0,98 con le
  normali vere ma scende a 0,71-0,98 con quelle stimate (risente dell'orientamento), in 0,3-1,6 s e con 8000-18000
  triangoli. L'alpha shape di Open3D va male su sfera e scatola (0,20 e 0,52): su superfici senza punti interni perde
  gran parte delle facce.
- **Le uniche mesh generative complete** su queste forme (dalla Fase 1) sono **molto peggiori**: MeshAnything sulla
  scatola 0,23 in 74 s di GPU, FastMesh sul pupazzo 0,47 in 225 s di CPU. Lo stesso confronto attraverso il server: Ball
  Pivoting sulle nuvole della scena 0,88-0,95 in circa 180 ms totali per oggetto.
- **Cautele**: (1) l'F-score misura la fedeltà alla superficie, non la "qualità da artista" (topologia pulita, spigoli
  netti, poche facce ben disposte), che è il punto di forza dichiarato dei generativi; (2) le nuvole sono pulite e
  complete, il caso più favorevole ai metodi classici: con rumore e buchi i classici peggiorano, mentre i generativi
  "immaginano" una forma plausibile; (3) i tempi sono della CPU di questo PC, non del visore; (4) per i generativi ci
  sono due sole mesh complete, sulla GPU del laboratorio ce ne saranno per tutte le forme.
- **Cosa ne segue per la tesi**: su nuvole pulite un metodo classico, abbastanza leggero da poter forse girare sul
  visore, batte i generativi in accuratezza e di tre ordini di grandezza in tempo. L'Edge con un modello generativo ha
  senso solo dove il classico non basta: dati rumorosi o incompleti (scansioni vere), oppure se serve una mesh leggera
  e "pulita" che il classico non dà. Sono queste le domande da proporre a Claudia, insieme alla ricostruzione
  progressiva (risultato classico subito, generativo quando arriva).
- Il server ha il backend `--backend classical --method ball_pivoting` (anche `hull`, `alpha`, `poisson`,
  `--target-faces`): si può provare nel visore con le stesse etichette di accuratezza. Avvio 2,5 s (carica Open3D),
  poi circa 90-180 ms a oggetto.

### 4.7 Ball Pivoting nel visore, e perché le mesh hanno buchi (13 novembre)

Sessione `benchmarks/results/quest/20261113_112816_wifi/` (`--backend classical --method ball_pivoting`, Wi-Fi, scena
spostata): **tutti e sei gli oggetti diventano mesh in 0,2-0,9 s** dall'ingresso in vista (MeshAnything: 66 s per la
sola scatola). Per richiesta: server 140-240 ms (di cui ~55 ms per misurare l'accuratezza), rete 21-64 ms anche con
mesh da 55-73 KB (dieci volte quelle di MeshAnything), caricamento 28 ms, dalla cache 30 ms. **Gli fps restano a 72**
con sei mesh da 3-4 mila triangoli in vista (tempo dell'app ~7 ms per frame, GPU al 63%); solo il solito calo nei 2 s
dopo la prima mesh (52). Accuratezza come sul PC: F-score 0,88-1,00.

**Le mesh hanno buchi visibili**, e sono veri (il materiale disegna già entrambi i lati delle facce): da 6 piccoli
sulla sfera a 43-79 sugli altri oggetti, i più grandi sulle parti sottili (gatto: un buco di 624 lati, pupazzo 417).
Ball Pivoting non chiude mai la superficie: posa un triangolo dove una pallina tocca tre punti, e dove i punti sono
radi o le normali stimate si girano (zampe, coda, braccia) lascia il vuoto; il richiamo scende a 0,78-0,84 proprio su
bacchetta, pupazzo e gatto. Prove sulle stesse nuvole (normali stimate, come il server):

- pallina più grande (raggi fino a 8 volte la spaziatura): nessun effetto;
- **chiusura dei buchi** dopo la ricostruzione (`fill_holes` di Open3D, +10-20 ms): i buchi piccoli spariscono quasi
  tutti (scatola 43 -> 10, coniglio 79 -> 22, gatto 46 -> 9), accuratezza uguale o appena migliore; i grandi restano;
- Poisson senza rifilatura (superficie chiusa per costruzione): perfetto su scatola, sfera e coniglio, ma su pupazzo,
  gatto e bacchetta crolla a F-score 0,54-0,75, perché le normali girate sulle parti sottili lo fanno "gonfiare";
- **più punti** (nuvole della Fase 1, Ball Pivoting con normali stimate):

| Punti per oggetto (memoria) | Richiamo (coniglio / gatto / pupazzo / bacchetta) | F-score | Server | Triangoli |
|---|---|---|---|---|
| 500 (6 KB) | 0,78 / 0,60 / 0,54 / 0,46 | 0,63-0,84 | ~20 ms | 450-860 |
| 2048 (24 KB) | 0,92 / 0,87 / 0,79 / 0,79 | 0,87-0,95 | ~100 ms | 2800-3600 |
| 8192 (96 KB) | 0,99 / 0,99 / 0,95 / 0,95 | 0,97-0,995 | ~0,5 s | 13-15 mila |

**Cosa ne segue**: la qualità dei metodi classici dipende soprattutto da **quanti punti tiene il visore**. Con 8192
punti sono quasi perfetti; con pochi punti (500) lasciano buchi grandi, ed è lì che i modelli generativi, fatti per
completare forme da nuvole rade, potrebbero davvero servire (il loro costo, misurato nella Fase 1, non dipende dal
numero di punti). È una domanda precisa per la tesi: **sotto quale densità di punti un generativo sull'Edge batte un
classico?** Da misurare in laboratorio con lo sweep (`--points 500 2048 8192`) per entrambi.

**Poisson nel visore** (sessione `20261113_113343_wifi`, `--method poisson`, stesse condizioni), a confronto:

| | Ball Pivoting | Poisson |
|---|---|---|
| Dall'ingresso in vista alla mesh | 0,2-0,9 s | 0,4-1,7 s |
| Server per oggetto | 140-240 ms | 300-450 ms |
| Mesh: triangoli / dimensione | 2800-4100 / 54-72 KB | 8500-18600 / 151-329 KB |
| Triangoli in tutta la stanza | 20,7 mila | 82,6 mila |
| Rete (mediana) | 29 ms | 54 ms |
| fps a regime / tempo app per frame / GPU | 72 / 7,0 ms / 63% | 72 / 7,5 ms / 70% |
| Buchi per mesh | 6-79 (grandi sulle parti sottili) | 2-20 (piccoli, dalla rifilatura) |
| F-score | 0,88-1,00 | 0,56-0,98 (pupazzo 0,56, gatto 0,74) |

Poisson dà superfici quasi chiuse e lisce, ma con le normali stimate "gonfia" le parti sottili (pupazzo, gatto,
bacchetta) e costa quattro volte i triangoli e cinque volte i byte. **Il Quest 2 regge comunque 83 mila triangoli a
72 fps**: con queste scene il collo di bottiglia non è il disegno ma la qualità della ricostruzione.

**Alpha shape nel visore** (sessione `20261113_113713_wifi`, `--method alpha`): sei mesh in 0,2-0,7 s, 72 fps, ma
**la scatola resta con i soli contorni** (richiamo 0,42, F-score 0,52) e la sfera è quasi vuota (richiamo 0,09);
coniglio, gatto e bacchetta discreti (0,83-0,87), pupazzo 0,71. Il motivo: l'alpha shape divide lo spazio tra i
punti in tetraedri (Delaunay) e tiene solo quelli abbastanza piccoli (sfera circoscritta di raggio < alpha); la
superficie è la pelle dei tetraedri tenuti. Su una faccia piatta i punti stanno su un piano, i loro tetraedri sono
"piatti come un foglio" con sfere circoscritte enormi e vengono scartati: restano solo quelli vicino agli spigoli, dove
si incontrano punti di due facce. Lo stesso sulla sfera, un guscio liscio senza punti interni. L'implementazione di
Open3D tiene solo tetraedri interi (non i triangoli piccoli di tetraedri scartati), quindi qui l'alpha shape **non è
adatto**: resta nella tesi come risultato negativo. I candidati classici veri sono Ball Pivoting (fedele ma con buchi)
e Poisson (chiuso ma gonfia le parti sottili).

**Involucro convesso nel visore** (sessione `20261113_113918_wifi`, `--method hull`): il più veloce (8-23 ms di server
senza la misura dell'accuratezza), perfetto sugli oggetti convessi (sfera 1,00, scatola 0,98) e un "palloncino" sugli
altri (0,16-0,39). In questa sessione la rete è stata più lenta e variabile (mediana 96 ms, fino a 162) a parità di
condizioni: la variabilità del Wi-Fi va messa in conto anche quando le mesh sono piccole.

**Le quattro prove dei metodi classici nel visore** (13 novembre, Wi-Fi, stessa scena, 2048 punti, normali stimate
dal server), con MeshAnything V2 per confronto (sezione 4.4, sola scatola):

| Metodo | Server per oggetto (senza misura accuratezza) | F-score | Triangoli nella stanza | Mesh | Difetto visibile |
|---|---|---|---|---|---|
| Involucro convesso | 8-23 ms | 0,16-1,00 | 6 mila | 4-73 KB | riempie le concavità |
| Alpha shape | 28-138 ms | 0,16-0,87 | 16 mila | 7-72 KB | facce piatte e sfera mancanti |
| Ball Pivoting | 83-189 ms | 0,88-1,00 | 21 mila | 55-73 KB | buchi sulle parti sottili |
| Poisson | 244-397 ms | 0,56-0,98 | 83 mila | 152-330 KB | gonfia le parti sottili |
| MeshAnything V2 (GT 1030) | 66 s, solo la scatola | 0,70 | – | 7 KB | le altre forme non finiscono |

In tutte e quattro le sessioni l'app è rimasta a 72 fps, a parte il solito calo nei 2 s dopo la prima mesh.

## 5. Cosa NON è stato verificato

- Il **modello vero con il visore**: provato una volta, solo MeshAnything V2 e solo sulla scatola (sezione 4.4); gli
  altri modelli e le altre forme non finiscono in tempi utili su questa GPU.
- **Cavo contro Wi-Fi**: c'è una sola sessione per collegamento (5 richieste dopo la prima), con il backend mock, in un
  solo posto e momento (distanza dal router non annotata). Non provati: più sessioni, distanza, rete carica, mesh più grandi dei modelli veri.
- Il **tracciamento delle mani** (attivo nel progetto per Android).
- **Un altro PC**: `setup_pc` è stato provato solo qui (prova a vuoto, e verifica dei pesi senza rete: 35 s, nessun
  download, checksum uguali al lock); mai su Linux né su un PC nuovo. La sessione con `--server` è stata provata con
  server e client sullo stesso PC (scaricamento di righe e mesh, orologi), non ancora con due PC.
- Le nuvole sono **pulite** (campionate da mesh esistenti, senza rumore né buchi, normali stimate dal server):
  l'obiettivo reale potrebbe avere scansioni peggiori. È il caso più favorevole ai metodi classici (sezione 4.6).
- I **metodi classici sul visore**: misurati solo sulla CPU di questo PC; sul Quest non girano (Open3D non c'è in
  Unity: servirebbe una versione C# o un plugin nativo) e i loro tempi lì sono solo da stimare.
- I materiali generati hanno bisogno di *Render Face = Both* (le facce prodotte non hanno un'orientazione coerente
  per Unity); i modelli producono solo geometria, **nessun colore o texture**.

## 6. Da fare (in ordine suggerito)

1. Cavo contro Wi-Fi: primo confronto fatto (sezione 4.2). Per numeri da tesi ripetere 3 sessioni per collegamento
   (`session_usb.bat` / `session_wifi.bat`), annotando distanza dal router e altri dispositivi.
2. **Correzioni dell'app: fatte e provate in parte sul visore (sezione 4.5).** Resta il calo di fps alla prima
   mesh, che la preparazione all'avvio non toglie. Coda: prima l'oggetto
   più vicino al centro della vista (*Queue Order*, `Nearest` per il comportamento di prima); gli oggetti che il
   server abbandona per il limite di tempo (504) o rifiuta (4xx) non vengono più richiesti, gli errori passeggeri
   con una pausa che raddoppia (sezione 4.3). L'attesa misurata non riparte mentre la richiesta è in corso (sezione
   4.4). All'avvio una mesh minuscola invisibile prepara glTFast (`WarmUpMesh.cs`): il primo caricamento
   ora costa 28 ms come gli altri, ma il calo di fps resta e ha un'altra causa. Tolto il vecchio test di un solo
   oggetto (`MeshLoader`, `EdgeMeshClient.cs` e i suoi file in `StreamingAssets`). Opzionale: un backend
   "replay" che restituisce mesh già generate con ritardi realistici, per le dimostrazioni.
3. Modello vero con il visore: fatto per la scatola con MeshAnything V2 (sezione 4.4, comando nel README). Restano
   gli altri modelli, la mesh completa di TreeMeshGPT e soprattutto il PC del laboratorio, per i numeri sulla GPU
   potente con tutti gli oggetti. **Il progetto è pronto per un altro PC** (12 novembre): `setup_pc.sh` /
   `setup_pc.bat` lo prepara con un comando (README, sezione 0), i pesi possono arrivare da un disco esterno
   (`--export-checkpoints` qui, `--checkpoints-from` là) e il visore può stare sul PC del laboratorio oppure su questo
   portatile con il server là (`session_wifi.bat --server http://<indirizzo>:8000`, README sezione 7). La tabella
   della tesi (tempo, memoria e accuratezza per forma e per modello, senza visore) si fa con lo sweep della Fase 1,
   `run_benchmarks`, sulle sei forme della scena: comando nel README, sezione 5. Da novembre misura anche con le
   **normali stimate** dai punti (`--normals estimate`), come succede col visore: tutti i numeri della Fase 1 usano
   invece le normali vere della mesh. **Prima prova** (12 novembre, GT 1030, MeshAnything V2, scatola della Fase 1,
   2048 punti): con le normali vere 76 s, 336 triangoli, F-score 0,23, identico alla Fase 1; con quelle stimate la
   generazione **non finisce** entro 240 s (2812 token contro 1596). Eppure la scatola della scena, stessa forma e
   stesse dimensioni ma un altro campionamento di punti, con le normali stimate dal server finisce in 66 s con F-score
   0,70 (sezione 4.4). Le normali stimate si scostano di oltre 10° da quelle vere sul 38% dei punti, sugli spigoli.
   Conclusione provvisoria: MeshAnything reagisce molto a piccole differenze nell'ingresso, quindi in laboratorio un
   solo run per forma non basta: servono più campionamenti dei punti e tutte e due le normali.
4. Accuratezza e memoria con le mesh dei modelli, variando il numero di punti. **Da novembre il server misura da solo
   l'accuratezza** di ogni mesh rispetto all'originale (F-score, precisione, richiamo, Chamfer: README, sezione 8) e
   salva le mesh nella cartella della sessione; `summary.md` le riporta in una tabella. Nel visore: riga con il tempo
   trascorso e la stima di quanto manca sul pannello, etichetta con l'accuratezza sopra ogni oggetto (viste sul
   visore e nel simulatore). Riferimento minimo (sessione mock `20261112_113036_wifi`), F-score / Chamfer-L2 dell'involucro convesso: sfera
   1,00 / 0,00003, scatola 0,98 / 0,00005 (forme convesse: l'involucro è già la risposta giusta), coniglio 0,39 /
   0,0034, bacchetta 0,31 / 0,0023, pupazzo 0,26 / 0,0041, gatto 0,16 / 0,0072. Per confronto, forme volutamente
   sbagliate sul coniglio: triangoli a caso nel suo riquadro 0,15 / 0,0057, ellissoide 0,11 / 0,0121, scatola 0,06 /
   0,0268; una ricostruzione buona (FastMesh sul pupazzo, Fase 1) 0,47 / 0,00041. Il Chamfer da solo inganna (i
   triangoli a caso sono vicini all'involucro): vanno riportati tutti e due. Il calcolo costa 50-350 ms per mesh.
   Nel simulatore, con l'editor in Play sulla stessa GT 1030, la scatola non finisce in 120 s (504,
   `20261112_113836_wifi`) contro i 66 s col visore: l'Edge deve avere la GPU tutta per sé.
5. Confronto **Edge contro inferenza sul Quest** (proposta dello studente, ancora senza risposta della relatrice):
   nel progetto c'è già il pacchetto Unity Inference Engine (`com.unity.ai.inference`).
6. Robustezza: nuvole con rumore e buchi, scena reale. **Dopo la sezione 4.6 è l'esperimento più importante**:
   decide se i modelli generativi servono a qualcosa rispetto ai metodi classici.
7. Scrittura della tesi.

**Domande ancora aperte per Claudia**: tempo di attesa accettabile e numero di oggetti per scena; cosa mostrare
nell'attesa (finora: i punti); come gestire colori e texture; confronto Edge contro Quest; accesso al PC del
laboratorio (offerto, "se non si riesce con il tuo"). **Da proporle** (sezione 4.6): il confronto con i metodi
classici come asse della tesi, input realistici (rumore, buchi, scansioni vere), ricostruzione progressiva.

## 7. Contesto pratico

- Windows 11, Python 3.11 (`.venv`), Unity 6.3 LTS (6000.3.26f1), progetto in `unity/OnDemandMeshVR`, repository
  `plg2001/ondemand-mesh-vr`, ramo `main`. Il disco è stato quasi pieno: tra pesi (10,7 GB), `.venv` (6 GB) e cache
  di Unity (4 GB e in crescita) il progetto supera i 20 GB; su GitHub ne finiscono circa 100 MB.
- Quest 2 con Android 14 (API 34). **Un controller ha lo stick difettoso (deriva)**: mai dipendere dall'analogico;
  si cammina e si gira fisicamente. Il gamepad PS4 è stato considerato e rimandato.
- Tre file che Unity modifica da solo **non vanno committati**: `UnityConnectSettings.asset` (servizi online
  attivati), `ProjectSettings.asset` (lista `preloadedAssets` svuotata dal gestore XR) e
  `Assets/XR/Settings/OpenXRPackageSettings.asset` (identificativi rinumerati). Si scartano con Unity chiuso.
- **Portabilità** (12 novembre): niente dipende più da questo PC. Percorsi relativi al progetto, pesi e cache in
  `checkpoints/` alle revisioni di `models/upstream.lock.json` (prima `setup_models.py` scaricava l'ultima versione
  da Hugging Face, mentre i wrapper caricano quella fissata), `adb` cercato su Windows, Linux e macOS, lanciatori
  `.bat` e `.sh` con i fine riga fissati da `.gitattributes`, orari dei log assoluti (`t_epoch`) e fuso del visore
  registrato, così i riassunti escono uguali su qualunque PC.
- Documentazione e README in italiano, codice e commenti in inglese. Lo studente non conosceva Unity né i visori:
  spiegare i passaggi uno per uno, nominare ogni file e dire sempre cosa è **verificato** e cosa è solo previsto.

## 8. Comandi utili

```powershell
python -m pytest -q tests                      # 82 test, senza pesi né GPU
setup_pc.bat                                   # prepara o controlla il PC (Linux/macOS: ./setup_pc.sh)
python tools\setup_pc.py --export-checkpoints E:\checkpoints   # pesi su disco esterno per un altro PC
start_edge_server.bat                          # server mock per le prove nell'editor
tools\quest\check_headset.bat                  # il Quest è collegato e autorizzato?
tools\quest\server_usb.bat                     # server + collegamento via cavo (prima prova)
tools\quest\server_wifi.bat                    # server raggiungibile dal Wi-Fi
tools\quest\install_app.bat                    # installa Builds\OnDemandMeshVR.apk
tools\quest\pull_results.bat                   # copia i CSV dei tempi dal visore
tools\quest\session_usb.bat                    # sessione di misura via cavo (anche session_wifi.bat)
tools\quest\session_wifi.bat --server http://<ip>:8000   # sessione con il server su un altro PC
python -m benchmarks.quest_report <cartella>   # rigenera summary.md di una cartella in benchmarks/results/quest
python data\scenes\build_scene.py              # rigenera la scena di prova
```

In Unity: **Tools → Quest → Prepare and build APK** (la prima build è durata 35 minuti; le successive dovrebbero
essere più brevi, ma non è stato misurato).
