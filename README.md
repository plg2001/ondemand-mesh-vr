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

## 0. Su un PC nuovo (Windows, Linux, macOS): un comando

```bash
git clone https://github.com/plg2001/ondemand-mesh-vr.git && cd ondemand-mesh-vr
./setup_pc.sh                                   # Linux / macOS   (Windows: doppio clic su setup_pc.bat)
./setup_pc.sh --checkpoints-from /media/usb/checkpoints   # pesi da un disco esterno invece che da internet
```

`tools/setup_pc.py` prepara tutto e si può rilanciare (salta quello che è già fatto; su questo PC controlla soltanto):
ambiente `.venv` con **Python 3.11** (con `uv`, se c'è, che scarica da solo il 3.11; altrimenti `py -3.11` /
`python3.11`); **PyTorch 2.7.1 per la scheda del PC** (CUDA 12.8 per le RTX 50, CUDA 11.8 per le altre NVIDIA dalla
GT 1030 alla RTX 40, CPU senza NVIDIA; `--cuda` la forza) e poi `requirements.txt`; codice dei modelli ai commit
fissati e **pesi alle revisioni di `models/upstream.lock.json`** (stessi file su ogni PC, controllati con sha256); nuvole
di prova e scena con le nuvole di riferimento per l'accuratezza; test. Opzioni: `--dry-run` (mostra soltanto),
`--skip-weights`, `--no-tests`.

**Pesi su disco esterno** (11 GB, utile se la rete del laboratorio è lenta o Google Drive blocca TreeMeshGPT): su questo
PC `python tools/setup_pc.py --export-checkpoints E:/checkpoints` copia solo i 6 file dei pesi (10,6 GB, senza i
doppioni della cache di Hugging Face); sull'altro PC `--checkpoints-from <quella cartella>` li copia e li verifica senza
scaricare nulla. Su Ubuntu senza Python 3.11: `sudo add-apt-repository ppa:deadsnakes/ppa && sudo apt install
python3.11 python3.11-venv`, oppure installare [uv](https://docs.astral.sh/uv/).

Tutto il resto è indipendente dal sistema: percorsi relativi alla cartella del progetto, pesi e cache dentro
`checkpoints/`, orari dei log in secondi dal 1970 (`t_epoch`) e fuso del visore registrato a ogni sessione, `adb`
cercato nei posti usuali di Windows, Linux e macOS, lanciatori `.bat` (Windows) e `.sh` (Linux/macOS) con i fine riga
giusti grazie a `.gitattributes`.

## 1. Ambiente (Windows 11, Python 3.11)

A mano, se non si usa `setup_pc` (su questo PC è già fatto):

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
- L'output va in `benchmarks/results/<timestamp>/`: `results.csv`, `results.json`, `system.json` e `summary.md`. Un
  run che non finisce lascia il suo output in `runs/<configurazione>.log.txt` e il motivo compare a schermo.
- Prima di partire `test_inference.py` stima la memoria necessaria e rinuncia se non basta: sulla GT 1030
  MeshAnything V2 è appena sopra la stima (1,77 GB contro 1,60 liberi) ma in pratica ci sta, quindi qui serve
  `--force`; sulla GPU del laboratorio no.
- `--normals file|estimate` (anche tutti e due: una configurazione ciascuno): `file` dà ai modelli le normali vere
  della mesh originale, `estimate` le stima dai punti con Open3D, **come fa il server per il visore**, che manda solo
  i punti. Il default `auto` usa quelle del file (così sono stati misurati tutti i risultati della Fase 1). La tabella
  ha una colonna *Normali*; TreeMeshGPT 7 bit non le usa.

**La tabella della tesi** (sul PC del laboratorio, nessun visore): le sei forme della scena di prova (`box`, `bunny`,
`fastmesh_001` = pupazzo, `sphere`, `tmg_cat` = gatto, `mav2_wand` = bacchetta), i tre modelli, accuratezza, normali
vere e stimate. Circa un'ora sulla GPU:

```bash
.venv/bin/python -m benchmarks.run_benchmarks --modes real --device cuda --points 2048 --quality --time-budget-s 300 --repeats 3 --warmup 1 --shapes box bunny fastmesh_001 sphere tmg_cat mav2_wand --normals file estimate
```

**Metodi classici** (`models/mesh_wrappers/classical.py`, Open3D, CPU): involucro convesso, alpha shape, Ball
Pivoting, Poisson, sugli stessi ingressi, riferimento e misura dei generativi, per rispondere a "serve davvero un
modello generativo?". Normali vere e stimate, piena risoluzione e ridotta a 1000 triangoli, e nella stessa tabella le
mesh complete dei generativi già misurate (risultati e commento: `docs/stato_progetto.md`, sezione 4.6). Un paio di
minuti su questo PC:

```powershell
python -m benchmarks.classical_baselines                          # sei forme della scena, 2048 punti
python -m benchmarks.classical_baselines --shapes bunny --points 500 2048 --target-faces 500 1000
python -m edge.server --backend classical --method ball_pivoting  # gli stessi metodi dal server, anche per il visore
```

## 6. Test

```powershell
python -m pytest -q tests
```

## 7. Nodo Edge Linux (GPU NVIDIA): il PC del laboratorio

Sul nodo Linux gira lo stesso codice: si prepara con `./setup_pc.sh` (sezione 0). Su GPU Ampere o successive (sm80+)
si può aggiungere il kernel FlashAttention vero (`--attn auto` lo usa se c'è, altrimenti lo shim SDPA):

```bash
.venv/bin/pip install flash-attn --no-build-isolation   # opzionale, solo sm80+ (compilazione lunga)
.venv/bin/python -m benchmarks.run_benchmarks --modes real --device cuda --precision fp16 --repeats 3 --warmup 1 --quality
```

Su Linux il profiler legge anche l'energia: della GPU via NVML (Volta+) e della CPU via RAPL.

**Con il visore**, due modi:

1. **Visore collegato al PC del laboratorio** (più semplice, niente rete da configurare): lì gli strumenti del Quest
   funzionano uguali (`adb` con `sudo apt install adb`; se dice *no permissions*, una regola udev per il vendor 2833),
   per esempio `.venv/bin/python tools/quest/quest.py session usb --server-args "--backend model --model meshanything"`.
   Serve anche Unity solo se si vuole ricostruire l'app: l'APK già installato sul visore basta.
2. **Visore collegato a questo portatile, server sul PC del laboratorio**: sul laboratorio
   `./start_edge_server.sh --host 0.0.0.0 --backend model --model meshanything --save-meshes benchmarks/results/edge_meshes`
   (con `ufw` attivo: `sudo ufw allow 8000/tcp`); sul portatile
   `tools\quest\session_wifi.bat --server http://<indirizzo del laboratorio>:8000`. La sessione non avvia nessun
   server, misura la differenza tra gli orologi di visore, portatile e server, e alla fine scarica dal server le righe
   del suo log e le mesh di questa sessione (`GET /requests`, `GET /meshes/...`). Le reti universitarie spesso
   isolano i dispositivi: se il visore non raggiunge il server, serve un router proprio (o il modo 1).

## 8. Prototipo on-demand: server Edge + client Unity

Il visore (il Quest 2, oppure Unity sul PC con l'XR Device Simulator) manda una nuvola di punti al server Edge, che
risponde con la mesh ricostruita in formato `.glb`; Unity la carica a runtime.

```
Unity (SceneObject)     ── punti float32, ~24 KB ──►  server Edge (python -m edge.server)
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
python -m edge.server --save-meshes out/meshes              # salva anche ogni mesh inviata (.glb)
python -m edge.server --no-quality                          # niente misura dell'accuratezza (latenza pura)
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

**Accuratezza.** Se l'oggetto è uno della scena (`--scene`, default `demo_room`) il server confronta la mesh con
quella originale, cioè con la nuvola densa che `build_scene.py` ne ha campionato (`data/scenes/<scena>/<id>_ref.npy`,
stesso sistema di riferimento dei punti e della mesh): F-score all'1% della diagonale, precisione (la mesh non
inventa parti), richiamo (non ne dimentica), Chamfer-L2. Vanno negli header `X-FScore`, `X-Precision`, `X-Recall`,
`X-Chamfer-L2` e nel log; `X-Quality-Ms` è il tempo del calcolo (50-350 ms, cresce con i triangoli), **compreso** in `X-Server-Ms`: va tolto per
la latenza della sola ricostruzione. Le nuvole di riferimento non sono versionate: su un altro PC (laboratorio) vanno
generate con `python data/scenes/build_scene.py`, altrimenti il server lo dice e salta la misura. Con il mock
l'involucro convesso fa da riferimento minimo: F-score 1,00 sulla sfera e 0,98 sulla scatola (convesse), 0,39 sul
coniglio, 0,31 sulla bacchetta, 0,26 sul pupazzo, 0,16 sul gatto. `/health` dà anche `typical_s` (mediana del tempo del server sulle ultime mesh; col mock il suo ritardo) e
`budget_s` (limite di tempo, 0 = nessuno), che l'app usa per stimare quanto manca.

**Client Unity** (`unity/OnDemandMeshVR/Assets/Scripts/`): la scena con più oggetti descritta qui sotto. Per ogni mesh
ricevuta la Console di Unity riporta i KB inviati e ricevuti, il round trip diviso in tempo del server e tempo di rete,
e il tempo di caricamento del `.glb`; le stesse misure vanno in `edge_timings.csv`, in `Application.persistentDataPath`
(il percorso viene stampato nella Console alla prima richiesta). Gli endpoint `/shapes/...` (nuvole salvate sull'Edge)
non sono usati dall'app: restano per l'esperimento facoltativo "i punti stanno sull'Edge".

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
vista, azzurro = in vista e in attesa, giallo = richiesta in corso, verde = mesh mostrata, rosso = errore (verrà
richiesto di nuovo), magenta = errore, non più richiesto.

*Mode* sceglie il comportamento:

| Mode | Cosa fa |
|---|---|
| `OnDemand` (default) | ogni *Check Interval Seconds* (0,1 s) controlla quali oggetti sono nel campo visivo della camera; chiede all'Edge solo quelli senza mesh, uno alla volta, **prima quello più vicino al centro della vista** (*Queue Order* = `CenterOfView`; con `Nearest` prima il più vicino), e sostituisce i punti con la mesh. Un oggetto fallito viene richiesto di nuovo solo se può servire: server irraggiungibile (quando torna) o errore passeggero (pausa che raddoppia a ogni errore, al massimo 5 minuti). Quelli che il server ha abbandonato per il limite di tempo (504) o rifiutato (4xx, per esempio `--objects`) restano punti. Una mesh che resta fuori vista per *Release After Seconds* (3 s) viene eliminata e l'oggetto torna ai punti. Con *Cache Meshes* il `.glb` ricevuto resta in memoria: quando l'oggetto rientra in vista ricompare subito, senza nuova richiesta e senza attendere quelle in corso verso l'Edge. L'attesa misurata parte da quando l'oggetto entra in vista senza mesh e non si azzera se esce e rientra per poco, né finché la sua richiesta è in corso |
| `ReconstructAllOnStart` | chiede tutti gli oggetti all'avvio, dal più vicino, senza guardare il campo visivo |
| `Manual` | niente di automatico: si usa il menu del componente |

Altri parametri: *View Margin* (allarga il controllo del campo visivo, così la richiesta parte un po' prima),
*Max Distance* (oltre questa distanza gli oggetti restano punti), *Retry After Seconds* (prima pausa dopo un errore
passeggero), *Warm Up Mesh Loading* (all'avvio carica e disegna una volta, invisibile, una mesh minuscola fatta
dall'esportatore del server, `WarmUpMesh.cs`: così la prima mesh vera non paga l'avvio di glTFast e la preparazione
dello shader). Dal menu ⋮ del componente: *Reconstruct all objects*, *Remove all meshes*, *Retry failed objects*,
*Print memory report* (oggetti in vista, mesh mostrate e loro triangoli, memoria dei punti e della cache).

Nel visore il **pannello di stato** ha una riga per la richiesta in corso: oggetto, modello, secondi trascorsi e una
stima di quanto manca, per esempio `Reconstructing box_01 (MeshAnything V2): 23 s, about 43 s left (last time 66 s),
gives up at 120 s`. La stima è il tempo che lo stesso oggetto ha richiesto l'ultima volta con lo stesso modello
(ricordato dall'app tra un avvio e l'altro); se non è mai stato chiesto, il tempo tipico del server. Sopra ogni oggetto
con la mesh compare un'**etichetta** (*Show Object Labels*, `ObjectLabel.cs`) con modello, triangoli, tempo e
accuratezza misurata dal server (F-score, Chamfer, precisione, richiamo); è sempre rivolta verso chi guarda e ha la
stessa grandezza apparente da vicino e da lontano.

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
- per finire, **sempre Invio** nella finestra, mai la X: i risultati si salvano solo dopo Invio. Va bene anche
  togliere prima il visore: si perdono solo gli fps degli ultimi secondi (in standby non si disegna nulla);
- per un modello vero: `tools\quest\session_wifi.bat --server-args "--backend model --model meshanything --time-budget-s 300 --objects box_01"`.
  Sulla GT 1030 MeshAnything V2 ricostruisce la scatola in circa 70 s e non finisce le altre forme in 5 minuti;
  `--objects` rifiuta subito gli altri oggetti, invece di spendere 5 minuti per ognuno prima di arrendersi;
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
