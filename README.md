# ondemand-mesh-vr

Ricostruzione **on-demand** di mesh 3D da point cloud sparse per visori VR standalone
(Meta Quest 2 / Quest 3S) tramite inferenza assistita da un nodo **Edge**.

**Fase 1 (questo repository)**:
- integrazione di tre modelli generativi point-cloud→mesh dello stato dell'arte:
  [MeshAnything V2](https://github.com/buaacyw/MeshAnythingV2), [FastMesh](https://github.com/jhkim0759/FastMesh)
  e [TreeMeshGPT](https://github.com/sail-sg/TreeMeshGPT);
- una suite di benchmark che misura latenza, memoria e dimensione della mesh prodotta;
- una stima della fattibilità sul SoC dei visori, che motiva l'offloading sull'Edge.

Documentazione tecnica:
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
benchmarks/                 profiler, modello FLOPs/memoria, stimatore visore, sweep, riepiloghi
  device_profiles.yaml      Quest 2, Quest 3S, GPU edge di riferimento, macchina locale (ogni valore con fonte)
  results/                  report JSON/CSV/Markdown
docs/                       analisi dei requisiti e metodologia
tests/                      test unitari (pytest)
third_party/, checkpoints/  codice e pesi upstream (scaricati, non versionati)
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
