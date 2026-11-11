# ondemand-mesh-vr

Ricostruzione **on-demand** di mesh 3D da point cloud sparse per visori VR standalone
(Meta Quest 2 / Quest 3S) tramite inferenza assistita da un nodo **Edge**.

**Fase 1, costo dei modelli** (sezioni 1-7):
- integrazione di tre modelli generativi point-cloud→mesh dello stato dell'arte:
  [MeshAnything V2](https://github.com/buaacyw/MeshAnythingV2), [FastMesh](https://github.com/jhkim0759/FastMesh)
  e [TreeMeshGPT](https://github.com/sail-sg/TreeMeshGPT);
- una suite di benchmark che misura latenza, memoria e dimensione della mesh prodotta;
- una stima della fattibilità sul SoC dei visori, che motiva l'offloading sull'Edge.

**Fase 2, prototipo on demand** (sezione 8): server Edge, app Unity per il Quest 2 che chiede la mesh di un oggetto
quando entra nel campo visivo, strumenti per misurare le sessioni sul visore.

Documentazione:
- [`docs/stato_progetto.md`](docs/stato_progetto.md): **da leggere per primo**. Dove siamo, cosa è stato misurato, cosa non è verificato, cosa resta da fare.
- [`docs/requisiti_calcolo.md`](docs/requisiti_calcolo.md): analisi di VRAM/RAM, banda, FLOPs ed energia, e incompatibilità con ARM64/Horizon OS.
- [`docs/metodologia_benchmark.md`](docs/metodologia_benchmark.md): come si misura, le formule di stima e i limiti.
- [`docs/risultati_benchmark.md`](docs/risultati_benchmark.md): tabelle generate dai report in `benchmarks/results/final_*`.

**In sintesi (Fase 1):**
- **Costo indipendente dalla densità.** Il costo è dominato dal decoding autoregressivo, che è
  memory-bound: ogni token rilegge ~0,6 GB di pesi.
  - Non dipende dalla densità della nuvola: con 100, 500 o 2048 punti la latenza per token cambia di ±4%.
  - Dipende dal numero di facce generate.
- **Misure su hardware di classe mobile** (GeForce GT 1030, banda simile all'Adreno 650 del Quest 2):
  - MeshAnything V2 genera ~23 token/s, cioè 74 s per un semplice box;
  - su una forma più complessa non termina in 25 minuti.
- **Misure su CPU desktop:**
  - FastMesh produce una mesh completa (1 112 vertici) in 225 s;
  - MeshAnything V2 e TreeMeshGPT richiederebbero circa 7–8 minuti per 800 facce.
- **Stime roofline sul visore**, validate entro il 20% sulla GPU locale a contesto breve (ottimistiche
  a contesto lungo):
  - per una mesh da 800 facce in fp16, da ~27 s (FastMesh) a ~3,6 min (MeshAnything V2) sul Quest 2;
  - fino a ~2,6% di batteria per mesh;
  - dal 35% al 57% del budget di memoria dell'app.
- **Offloading.** Su un nodo Edge con GPU moderna la stessa generazione richiede secondi, e la rete
  (pochi KB in upload, decine di KB in download) è trascurabile.

---

## Struttura

```
models/                     wrapper dei modelli (NON è un package Python: vedi nota sotto)
  setup_models.py           clona i repo ufficiali a commit fissati + scarica i pesi (lock: upstream.lock.json)
  test_inference.py         CLI di inferenza / profiling (modalità real | mock | auto)
  specs/*.yaml              iperparametri architetturali (letti dai sorgenti ufficiali) per mock e stimatore
  mesh_wrappers/            wrapper per modello, shim FlashAttention-2→SDPA, I/O point cloud, export mesh
data/inputs/                generate_pointclouds.py → nuvole .ply/.xyz/.npy da 100 a 8192 punti + manifest.json
data/outputs/               mesh generate (.obj / .glb)
data/scenes/                build_scene.py + demo_room.yaml → scena di più oggetti salvati come nuvole sparse
benchmarks/                 profiler, modello FLOPs/memoria, stimatore visore, sweep, riepiloghi
  device_profiles.yaml      Quest 2, Quest 3S, GPU edge di riferimento, macchina locale (ogni valore con fonte)
  quest_report.py           riassunto di una sessione sul Quest (rete, caricamento, coda, fps)
  results/                  report JSON/CSV/Markdown: final_* = Fase 1, quest/ = sessioni sul visore
docs/                       stato del progetto, analisi dei requisiti, metodologia, risultati
edge/                       server Edge prototipo (FastAPI): nuvola di punti in ingresso, mesh .glb in uscita
tools/quest/                strumenti per il Quest (doppio clic): verifica, installazione, sessioni di misura,
                            server via cavo o Wi-Fi, log (vedi sezione 8)
unity/OnDemandMeshVR/       progetto Unity 6.3 (template VR): client che chiede le mesh al server e le mostra
tests/                      test unitari (pytest)
third_party/, checkpoints/  codice e pesi upstream (scaricati, non versionati)
                            checkpoints/huggingface/  = pesi MeshAnything V2, FastMesh, OPT (cache HF spostata qui)
                            checkpoints/treemeshgpt/  = pesi TreeMeshGPT (Google Drive)
```

> `models/` non contiene `__init__.py` di proposito: FastMesh ha un package top-level chiamato `models`,
> che altrimenti verrebbe oscurato dalla nostra cartella. I nostri moduli stanno in `models/mesh_wrappers/`.

## 1. Ambiente (Windows 11, Python 3.11)

```powershell
py -3.11 -m venv .venv                     # open3d 0.18 non ha wheel per Python >= 3.12
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
pip install --no-cache-dir -r requirements.txt   # torch 2.7.1+cu118 (~2.8 GB), include i kernel Pascal sm_61
python -c "import torch; print(torch.cuda.is_available(), torch.cuda.get_arch_list())"
```

- **flash-attn non serve e non va installato** su Windows o su GPU pre-Ampere.
  - MeshAnything V2 e FastMesh hanno `flash_attention_2` cablato nel codice.
  - `mesh_wrappers/flash_shim.py` lo sostituisce a runtime con `torch.nn.functional.scaled_dot_product_attention`.
  - Lo shim è numericamente equivalente: lo verifica `tests/test_flash_shim.py`.
- Spazio su disco necessario: circa 5 GB per l'ambiente e circa 11 GB per i pesi di default.

## 2. Codice e pesi dei modelli

```powershell
python models/setup_models.py                  # repo + pesi di default: MA V2, TreeMeshGPT 7bit, FastMesh V1K
python models/setup_models.py --skip-weights   # solo codice (basta per la modalità mock)
python models/setup_models.py --models fastmesh --fastmesh-variants V4K
python models/setup_models.py --models treemeshgpt --tmg-variants 9bit
```

| Modello | Pesi | Dimensione | Licenza |
|---|---|---|---|
| MeshAnything V2 | HF `Yiwen-ntu/meshanythingv2` | 2.0 GB | S-Lab 1.0 (non commerciale) |
| FastMesh V1K / V4K | HF `WopperSet/FastMesh-V1K` / `-V4K` | 4.4 GB ciascuno | S-Lab 1.0 (non commerciale) |
| TreeMeshGPT 7bit / 9bit | Google Drive (gdown) | 5.0 GB (7bit) | MIT |

Se gdown fallisce per la quota di Google Drive, scarica a mano il file indicato nel messaggio di errore e
salvalo in `checkpoints/treemeshgpt/`.

## 3. Point cloud di test

```powershell
python data/inputs/generate_pointclouds.py                      # bunny, box, sphere, tmg_cat, mav2_wand, fastmesh_001
python data/inputs/generate_pointclouds.py --shapes bunny armadillo --counts 100 500 2048 8192
python data/inputs/generate_pointclouds.py --list
```

Per ogni forma e ogni N vengono scritti tre file:
- `.ply`: xyz + normali;
- `.xyz`: solo coordinate, la rappresentazione "sparsa" da tenere sul visore;
- `.npy`: formato `pc_normal` (N×6).

Si aggiunge una nuvola densa di riferimento (50k punti) per le metriche di qualità.
`test_inference.py` genera da sé gli input mancanti.

## 4. Inferenza e profiling di un singolo caso

```powershell
# auto: real se repo, pesi e memoria disponibile lo consentono, altrimenti mock
python models/test_inference.py --model meshanything --points 500
python models/test_inference.py --model treemeshgpt --points 2048
python models/test_inference.py --model fastmesh --points 100

# mock / profiling: senza pesi né GPU
python models/test_inference.py --model fastmesh --points 2048 --mode mock --surrogate-layers 2

# reale su CPU, troncato a 64 token: misura ms/token reali e stima la generazione completa
python models/test_inference.py --model meshanything --points 2048 --mode real --device cpu --max-new-tokens 64 --force

# reale su GPU, generazione completa, con export .obj/.glb e metriche di qualità
python models/test_inference.py --model meshanything --shape mav2_wand --points 2048 --mode real --device cuda --precision fp16-full --quality
```

Flag principali:

| Flag | Significato |
|---|---|
| `--model` | `meshanything`, `fastmesh` o `treemeshgpt` |
| `--variant` | FastMesh: `V1K` o `V4K`; TreeMeshGPT: `7bit` o `9bit` |
| `--points N` | dimensione della nuvola sparsa (100, 500, 2048…) |
| `--input` / `--shape` | file esplicito, oppure forma presa da `data/inputs/manifest.json` |
| `--mode` | `auto`, `real` o `mock` |
| `--device` | `auto`, `cpu` o `cuda` |
| `--precision` | `fp32`, `fp16`/`bf16` (autocast con pesi fp32, come upstream), `fp16-full`/`bf16-full` (anche i pesi in mezza precisione) |
| `--max-new-tokens N` | run troncato, con estrapolazione della generazione completa |
| `--time-budget-s S` | interrompe la generazione dopo S secondi, conservando i tempi per token (run troncato) |
| `--assume-faces F` | facce in uscita ipotizzate, per mock e stime (default 800) |
| `--surrogate-layers k` | mock: cronometra k layer del decoder sul device reale e scala alla profondità piena |
| `--upsample repeat\|jitter` | come portare la nuvola sparsa al numero di punti nativo del modello (8192/4096) |
| `--normals auto\|file\|estimate` | normali dal file, oppure stimate con Open3D (KNN) |
| `--repeats R --warmup W` | ripetizioni, con media ± deviazione standard |
| `--quality` | Chamfer-L2 / F-score rispetto alla nuvola densa di riferimento |
| `--force` | tenta comunque l'esecuzione reale anche se la memoria stimata non basta (dimostrazione OOM) |
| `--json-out` / `--csv-out` | percorsi dei report |

Lo script stampa una tabella `Metrica | Valore` con:
- tempo di inferenza, diviso in encoder, prefill, decoding ed eventuale FaceGen;
- ms/token, picco VRAM (`torch.cuda.max_memory_allocated` / `reserved`) e picco RAM host;
- energia GPU (NVML, se il device la espone), FLOPs, vertici e triangoli, file `.obj`/`.glb`;
- stima per Quest 2 e Quest 3S, e confronto con l'offload su edge.

Il report JSON completo viene salvato in `benchmarks/results/runs/`.

## 5. Sweep di benchmark

```powershell
python -m benchmarks.run_benchmarks --modes mock --points 100 500 2048            # tutti i modelli, senza pesi
python -m benchmarks.run_benchmarks --modes real --device cpu --points 500 2048 --max-new-tokens 64 --force
python -m benchmarks.run_benchmarks --modes real --device cuda --repeats 3 --warmup 1 --quality   # nodo edge
python -m benchmarks.summarize "benchmarks/results/runs/*.json" --out docs/risultati_benchmark.md
python -m benchmarks.requirements_tables --faces 400 800 1500   # tabelle memoria/latenza/energia del visore
```

- Ogni configurazione gira in un processo separato, così l'OOM di un run non interrompe lo sweep.
- L'output va in `benchmarks/results/<timestamp>/`: `results.csv`, `results.json`, `system.json` e `summary.md`.

## 6. Test

```powershell
python -m pytest -q tests
```

## 7. Nodo Edge Linux (GPU NVIDIA)

Sul nodo Linux gira lo stesso codice. Su GPU Ampere o successive (sm80+) si può usare il kernel FlashAttention vero:

```bash
python3.11 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt            # oppure torch per cu121/cu124 se il driver lo richiede
pip install flash-attn --no-build-isolation  # opzionale, solo sm80+
python models/setup_models.py
python -m benchmarks.run_benchmarks --modes real --device cuda --precision fp16 --repeats 3 --warmup 1 --quality
```

Con `--attn auto` si usa flash-attn se presente e la GPU è sm80+, altrimenti lo shim SDPA. Su Linux il profiler
legge anche l'energia: della GPU via NVML (Volta+) e della CPU via RAPL.

## 8. Prototipo on-demand: server Edge + client Unity

Il visore (il Quest 2, oppure Unity sul PC con l'XR Device Simulator) manda una nuvola di punti al server Edge, che
risponde con la mesh ricostruita in formato `.glb`; Unity la carica a runtime.

```
Unity (EdgeMeshClient)  ── punti float32, ~24 KB ──►  server Edge (python -m edge.server)
                                                       modello: mock (inviluppo convesso) o rete vera
Unity (RuntimeMeshLoader) ◄──── mesh .glb ───────────  + header con i tempi di ogni fase
```

**Server**: il modo più semplice è il doppio clic su **`start_edge_server.bat`**, nella radice del repository.
Avvia il backend mock con 2 s di inferenza simulata per oggetto; se un server è già acceso sulla porta 8000 non ne
apre un secondo. Si ferma con `Ctrl+C` o chiudendo la finestra. Le opzioni scritte dopo il nome sostituiscono quelle
di default (`start_edge_server.bat --backend model --model meshanything`). Da terminale, dalla radice del repository:

```powershell
python -m edge.server                                       # backend mock: inviluppo convesso, niente pesi né GPU
python -m edge.server --mock-delay-s 3                      # mock con 3 s di inferenza simulata per richiesta
python -m edge.server --backend model --model meshanything  # modello vero (pesi in checkpoints/)
python -m edge.server --backend model --model fastmesh --time-budget-s 300   # risposta 504 dopo 5 minuti
python -m edge.server --backend model --model meshanything --objects box_01  # serve solo questi oggetti, gli altri 409
```

- Documentazione interattiva delle API, con la possibilità di provarle dal browser: http://127.0.0.1:8000/docs
- Il backend **mock** serve a sviluppare il protocollo sul PC: risponde in pochi ms con l'inviluppo convesso dei
  punti, nella stessa posizione e dimensione dell'oggetto. Il backend **model** carica la rete una sola volta
  all'avvio e la riusa per ogni richiesta.
- Le richieste vengono servite **una alla volta** (il modello occupa la GPU): quelle in attesa lo vedono in `X-Queue-Ms`.
- Con `--host 0.0.0.0` il server è raggiungibile dagli altri dispositivi della rete (per il Quest); serve anche
  aprire la porta nel firewall di Windows.

| Endpoint | Cosa fa |
|---|---|
| `GET /health` | stato del server, backend e modello caricati |
| `POST /reconstruct?format=xyz&object_id=...` | corpo = punti float32 little-endian (N×3, oppure N×6 con `format=xyzn`) → `.glb` |
| `GET /shapes` | nuvole salvate sul server (`data/inputs/manifest.json`) |
| `GET /shapes/{nome}/points?points=2048` | punti di una nuvola salvata sull'Edge (scenario "la scena sta sull'Edge") |
| `GET /shapes/{nome}/mesh?points=2048` | mesh ricostruita da una nuvola salvata sull'Edge |

Header della risposta, in millisecondi: `X-Queue-Ms`, `X-Preprocess-Ms`, `X-Inference-Ms`, `X-Postprocess-Ms`,
`X-Export-Ms`, `X-Server-Ms` (totale lato server). Il client calcola rete + overhead HTTP come
`round trip − X-Server-Ms`. Ogni richiesta è registrata anche in `benchmarks/results/tmp/edge_requests.csv`
(cartella non versionata), con l'indirizzo da cui è arrivata (`client`: `127.0.0.1` per l'editor e per il visore via
cavo, l'indirizzo del visore via Wi-Fi). Un log con le colonne di una versione precedente viene rinominato
(`edge_requests_old_<data>.csv`) invece di essere mescolato alle righe nuove.

**Client Unity, test di un solo oggetto** (`unity/OnDemandMeshVR/Assets/Scripts/`). È il primo client, superato dalla
scena con più oggetti descritta sotto: nella scena l'oggetto `MeshLoader` che lo contiene è **disattivato**.

1. Avvia il server (doppio clic su `start_edge_server.bat`).
2. Sull'oggetto `MeshLoader` della scena aggiungi il componente **Edge Mesh Client** (`EdgeMeshClient.cs`): usa il
   `RuntimeMeshLoader` dello stesso oggetto e ne disattiva il caricamento da file.
3. Premi Play: la nuvola indicata in *Point Cloud File* (in `Assets/StreamingAssets/PointClouds/`) viene inviata al
   server e la mesh ricevuta compare nella scena. Per ripetere la richiesta durante il Play: menu ⋮ del componente →
   *Request mesh from edge*.
4. Con *Source* = `UseCloudStoredOnEdge` il client non invia punti: chiede la nuvola per nome al server.

La Console di Unity riporta, per ogni richiesta, i KB inviati e ricevuti, il round trip diviso in tempo del server e
tempo di rete, e il tempo di caricamento del `.glb`. Le stesse misure vanno in `edge_timings.csv`, in
`Application.persistentDataPath` (il percorso viene stampato nella Console alla prima richiesta).

**Scena con più oggetti** (`data/scenes/`). Una scena nasce come mesh disposte in una stanza: un file YAML elenca
forma, posizione, rotazione e dimensione di ogni oggetto ([`data/scenes/demo_room.yaml`](data/scenes/demo_room.yaml):
6 oggetti). Lo script, lanciato una volta sola, campiona ogni oggetto in una nuvola sparsa e scrive la descrizione
della scena:

```powershell
python data/scenes/build_scene.py                           # demo_room: 6 oggetti x 2048 punti = 144 KB
python data/scenes/build_scene.py --points 500              # nuvole più sparse
```

- In `data/scenes/<nome>/` (non versionata): `scene.json`, una nuvola `<id>.xyz` per oggetto e una nuvola densa di
  riferimento `<id>_ref.npy` per le metriche di qualità.
- `scene.json` e le nuvole `.xyz` vengono copiate in `unity/OnDemandMeshVR/Assets/StreamingAssets/Scenes/<nome>/`.
- I punti di ogni oggetto stanno nel suo **sistema locale** (forma centrata, lato maggiore 1): sono quelli che il
  visore conserva e invia. Posizione, rotazione e scala, come in un nodo glTF, lo collocano nella stanza. Così la
  mesh che torna dall'Edge, anch'essa locale, si posiziona da sola.
- Riferimento: glTF (destrorso, +Y in alto, metri). Unity converte come glTFast fa per ogni `.glb`: posizione
  `(-x, y, z)`, rotazione `(x, -y, -z, w)`. Per questo un oggetto con x positivo nel YAML appare a sinistra in Unity.

In Unity: crea un GameObject vuoto in posizione `(0, 0, 0)`, aggiungi il componente **Point Cloud Scene**
(`PointCloudScene.cs`) e assegna `MeshGrey` a *Mesh Material*. Al Play crea un oggetto per ogni elemento della scena e
ne disegna la nuvola come **punti** (segnaposto): piccoli quadratini rivolti verso la camera, colore e diametro in
*Point Color* e *Point Size*. Con *Gizmos* attivo, il riquadro di ogni oggetto ne mostra lo stato: grigio = punti fuori
vista, azzurro = in vista e in attesa, giallo = richiesta in corso, verde = mesh mostrata, rosso = errore.

*Mode* sceglie il comportamento:

| Mode | Cosa fa |
|---|---|
| `OnDemand` (default) | ogni *Check Interval Seconds* (0,1 s) controlla quali oggetti sono nel campo visivo della camera; chiede all'Edge solo quelli senza mesh, uno alla volta, dal più vicino, e sostituisce i punti con la mesh. Una mesh che resta fuori vista per *Release After Seconds* (3 s) viene eliminata e l'oggetto torna ai punti. Con *Cache Meshes* il `.glb` ricevuto resta in memoria: quando l'oggetto rientra in vista ricompare subito, senza nuova richiesta e senza attendere quelle in corso verso l'Edge. L'attesa misurata parte da quando l'oggetto entra in vista senza mesh e non si azzera se esce e rientra per poco |
| `ReconstructAllOnStart` | chiede tutti gli oggetti all'avvio, dal più vicino, senza guardare il campo visivo |
| `Manual` | niente di automatico: si usa il menu del componente |

Altri parametri: *View Margin* (allarga il controllo del campo visivo, così la richiesta parte un po' prima),
*Max Distance* (oltre questa distanza gli oggetti restano punti), *Retry After Seconds* (dopo un errore).
Dal menu ⋮ del componente: *Reconstruct all objects*, *Remove all meshes*, *Print memory report* (oggetti in vista,
mesh mostrate e loro triangoli, memoria dei punti e della cache).

Ogni volta che una mesh compare, la Console stampa **quanto è passato da quando l'oggetto è entrato in vista**
(la latenza percepita), quanta parte di quel tempo l'oggetto ha atteso il proprio turno e se la mesh arriva dall'Edge o
dalla cache. Le stesse misure vanno in `ondemand_events.csv`, accanto a `edge_timings.csv` in
`Application.persistentDataPath`.

Per *Point Material* vuoto il componente crea a runtime un materiale `Universal Render Pipeline/Particles/Unlit`:
va bene nell'editor, ma per una build sul visore conviene assegnare un materiale, perché Unity può escludere dalla
build gli shader non usati.

**Sul Quest** (modalità sviluppatore attiva e debug USB autorizzato dentro il visore): la stessa app
gira sul visore. I file di `StreamingAssets` si leggono anche da dentro l'`.apk`, un pannello di stato sotto la linea
dello sguardo mostra server, oggetti e fps, e i materiali dei punti e del pannello stanno in `Assets/Resources` per
finire sempre nella build. In Unity, **Tools → Quest → Prepare and build APK** (`Assets/Editor/QuestBuild.cs`)
applica le impostazioni per il Quest (API minima 29, Internet, IL2CPP/ARM64, simulatore `EditorOnly`) e costruisce
`unity/OnDemandMeshVR/Builds/OnDemandMeshVR.apk`, senza bisogno del visore. Gli strumenti in `tools/quest/` (doppio
clic) usano `adb` di Unity:

| Script | Cosa fa |
|---|---|
| `check_headset.bat` | visore collegato e autorizzato? Android abbastanza recente? app installata? |
| `install_app.bat` | installa sul visore `Builds\OnDemandMeshVR.apk` (costruito da Unity) e lo avvia |
| `server_usb.bat` | il visore raggiunge il server **attraverso il cavo** (`adb reverse`), poi avvia il server |
| `server_wifi.bat` | scrive sul visore l'indirizzo del PC in rete (`edge_server.txt` nei dati dell'app, letto all'avvio: non serve rifare la build) e avvia il server raggiungibile dal Wi-Fi |
| `show_logs.bat` | la Console dell'app sul visore, in diretta |
| `pull_results.bat` | copia i CSV dei tempi dal visore in `benchmarks/results/quest/<data_ora>/` |
| `session_usb.bat`, `session_wifi.bat` | una sessione di misura completa: collegamento, server con un log suo, riavvio dell'app, fps registrati dal visore; premendo Invio copia tutto in `benchmarks/results/quest/<data_ora>_usb` (o `_wifi`) e scrive `summary.md` |

Il riassunto si può rigenerare, anche per le cartelle vecchie, con
`python -m benchmarks.quest_report benchmarks/results/quest/<cartella>`.

Note pratiche per le sessioni:
- il cavo resta collegato anche nella sessione Wi-Fi: porta solo il registro degli fps, le richieste passano dal Wi-Fi;
- premere **Invio** nella finestra **prima** di togliere il visore (in standby non ci sono fps) e non chiuderla con la
  X (i risultati si salvano dopo Invio);
- per un modello vero: `tools\quest\session_wifi.bat --server-args "--backend model --model meshanything --time-budget-s 300 --objects box_01"`.
  Sulla GT 1030 MeshAnything V2 ricostruisce la scatola in circa 70 s e non finisce le altre forme in 5 minuti;
  `--objects` rifiuta subito gli altri oggetti, che altrimenti, essendo più vicini, bloccherebbero la coda;
- Wi-Fi: la prima volta Windows Defender chiede l'accesso, va consentito sulle **reti private**, e la rete del PC deve
  essere *privata* (*Impostazioni → Rete e Internet → Wi-Fi*); prova da telefono: `http://<ip del PC>:8000/health`;
- l'orologio del PC può essere sbagliato di qualche secondo: la sessione misura la differenza col visore e il
  riassunto la corregge (avvisa se cambia durante la sessione);
- lo stick difettoso non serve: sul visore l'app spegne lo spostamento con gli stick (`sticks off` sul pannello), ci si
  muove camminando.

Limiti del prototipo:
- le misure lato Unity riguardano il dispositivo su cui gira: il PC nell'editor, il Quest nella build;
- il progetto Unity consente HTTP non cifrato (*Allow downloads over HTTP* = *Always allowed*): accettabile in
  una rete locale di laboratorio, non per un'app pubblicata.

## Note e limiti noti

- **Windows e VRAM**: con la "CUDA Sysmem Fallback Policy" del driver (attiva di default) le allocazioni
  oltre la VRAM finiscono nella RAM di sistema invece di dare OOM.
  - Il run "funziona", ma molto più lentamente.
  - Il report lo segnala con `vram_spilled_to_system_ram`.
  - Per ottenere l'OOM classico: Pannello di controllo NVIDIA → Gestisci impostazioni 3D → CUDA Sysmem Fallback Policy → *Prefer No Sysmem Fallback*.
- Con 8 GB di RAM chiudi le altre applicazioni prima dei run reali, in particolare per FastMesh (1.1 B parametri, 4.4 GB in fp32).
  - I pesi vengono caricati in streaming, così il picco non raddoppia.
  - TreeMeshGPT viene caricato in mmap.
  - Il working set può comunque andare in paging.
- I sorgenti upstream non vengono modificati: tutti gli adattamenti sono patch a runtime in `models/mesh_wrappers/`.
