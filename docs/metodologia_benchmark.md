# Metodologia di benchmark

Questo documento descrive *come* la suite misura e stima le grandezze riportate nei report
(`models/test_inference.py`, `benchmarks/`). Serve a rendere i numeri riproducibili e a
chiarire i limiti di validità delle estrapolazioni.

## 1. Pipeline misurata

```
nuvola sparsa (N punti) ─► preprocessing ─► encoder point cloud ─► prefill ─► decoding AR (T step) ─► [refine + FaceGen] ─► post-processing ─► export .obj/.glb
                          normali, ricampionamento a 8192/4096,   (Michelangelo   (token di        (1 token per step)      (solo FastMesh)        merge vertici,
                          normalizzazione del modello              / CloudEncoder)  condizione)                                                     facce degeneri
```

Il ricampionamento al numero di punti nativo del modello è obbligatorio, perché gli encoder sono stati
addestrati con 8192 punti (4096 per TreeMeshGPT 9bit).
- Una nuvola da 100 punti viene portata a 8192 duplicando i punti (`--upsample repeat`, default: non
  inventa geometria) oppure duplicando con rumore gaussiano (`jitter`).
- Il costo di calcolo quindi **non dipende da N**: dipende dal numero T di token generati, che cresce
  con le facce in uscita.
- N influenza solo l'informazione disponibile, cioè la qualità della mesh.

## 2. Tempi

- I tempi sono wall-clock (`time.perf_counter`) con `torch.cuda.synchronize()` prima e dopo ogni
  misura sulla GPU.
- **Tempi per stadio.** `benchmarks/profiler.StageTimer` avvolge i callable rilevanti del codice
  ufficiale senza modificarlo:
  - `point_encoder.encode_latents`;
  - `decoder.forward`, a cui corrisponde una chiamata per step autoregressivo; la prima chiamata è il prefill dei token di condizione;
  - `refine_vertices` e `face_gen.forward` per FastMesh;
  - `adjacent_detokenize` per MeshAnything.
- **ms/token.** Se ne riportano media, p50, p95 e la media del primo e dell'ultimo 10% degli step.
  Il confronto tra primo e ultimo 10% mostra la crescita della latenza con il contesto: la KV cache
  si allunga a ogni token.
- **Run troncati** (`--max-new-tokens`). La generazione completa si stima come
  encoder + prefill + p50(ms/token) × T_atteso, con T_atteso = facce ipotizzate × token/faccia
  (dalle specs, oppure misurato nei run completi).
  - Si usa la mediana perché i primi step possono includere page fault: i pesi caricati in mmap
    vengono letti dal disco al primo accesso.
- **Ripetizioni.** `--repeats R --warmup W` dà media ± deviazione standard; il warm-up elimina gli
  effetti di prima esecuzione (contesto CUDA, cache, mmap).

## 3. Memoria

| Grandezza | Fonte |
|---|---|
| Picco VRAM allocata / riservata | `torch.cuda.max_memory_allocated` / `max_memory_reserved` (reset prima di ogni run) |
| VRAM dei soli pesi | `torch.cuda.memory_allocated` dopo il caricamento |
| Memoria del device (incluso il contesto CUDA) | NVML `nvmlDeviceGetMemoryInfo`, campionato ogni 50 ms |
| RAM host | working set / RSS (psutil, campionato ogni 20 ms), memoria privata committed (Windows `private`), picco del sistema operativo (`peak_wset` / `ru_maxrss`) |

- **Su Windows** il working set può essere molto più basso della memoria allocata quando il sistema
  fa paging: per questo si riporta anche la memoria privata.
- **Spill della VRAM.** Con la *CUDA Sysmem Fallback Policy* del driver WDDM, le allocazioni oltre la
  VRAM finiscono nella RAM condivisa invece di sollevare OOM. Il report segnala
  `vram_spilled_to_system_ram` quando il picco riservato supera la VRAM fisica.
- **Caricamento.** I pesi si caricano in streaming:
  - `accelerate.init_empty_weights` crea i parametri su device meta;
  - i tensori arrivano uno alla volta da safetensors.
  In questo modo il picco di caricamento non raddoppia.

## 4. Energia

- **GPU.** Il contatore NVML `nvmlDeviceGetTotalEnergyConsumption` (Volta+) misura la differenza tra
  inizio e fine; in alternativa si integra la potenza campionata.
  - La GeForce GT 1030 non espone né l'uno né l'altra: il report indica `unsupported_by_device`.
- **CPU.** Si usa RAPL (`/sys/class/powercap/intel-rapl:0`) solo su Linux.
- **Visore.** L'energia si *stima* come P_SoC × t (§6).

## 5. FLOPs e parametri

- **Parametri esatti.** Si istanzia l'architettura ufficiale con `accelerate.init_empty_weights`, senza
  memoria né pesi. I valori sono riportati in `models/specs/*.yaml → params_measured`.
- **FLOPs analitici** (1 MAC = 2 FLOP), in `benchmarks/flops.py`:
  - step di decoding con contesto c:
    `F(c) = 2·P_layers + 4·L·d·(c+1) + 2·d·V`;
  - prefill / encoder bidirezionale su n token:
    `2·n·P_layers + 4·L·d·n²` (×½ se causale);
  - FaceGen aggiunge il termine `2·V²·d` per la matrice di adiacenza;
  - encoder perceiver: proiezioni K/V sugli N punti + cross-attention M×N + self-attention sui latenti.
- **Validazione.** Le formule sono confrontate con `torch.utils.flop_counter.FlopCounterMode` su un
  transformer surrogato con le stesse dimensioni, istanziato su device `meta`, quindi a memoria zero.
  `tests/test_flops.py` impone uno scarto inferiore al 2%; quello osservato è circa 0.1%.

## 6. Stima sul visore (roofline)

Per ogni step autoregressivo, con batch 1:

```
t_step(c) = max( F(c) / (P_picco · η_c) ,  (B_pesi + B_KV(c)) / (BW · η_m) ) + overhead_dispatch
T_totale  = [ prefill + Σ_t t_step(C+t) + componenti one-shot ] / fattore_termico
E         = P_SoC · T_totale        →  % batteria per mesh = E / (Wh · 3600)
```

- Il decoding a batch 1 è **memory-bound**: a ogni token si rileggono tutti i pesi del decoder e
  l'intera KV cache. Domina quindi il termine di banda.
- **Memoria richiesta** = pesi (fp16 o int4) + KV cache a fine generazione + attivazioni + runtime.
  Si confronta con il **budget di memoria per app** documentato da Meta (PSS): 4.4 GiB su Quest 2,
  5.75 GiB su Quest 3/3S.
- **Scenari.**
  - `nominal_fp16`: valori nominali.
  - `best_int4`: pesi a 4 bit, prestazioni al massimo dell'intervallo, overhead minimo. È un limite
    inferiore ottimistico, e assume senza dimostrarlo che la qualità resti invariata.
  - `worst_fp16`: limiti peggiori degli intervalli.
- **Offload edge** = upload della nuvola sparsa + calcolo sull'edge (misurato su GPU sm80+, altrimenti
  stima roofline RTX 4090) + download del GLB, con i parametri Wi-Fi 6 di `device_profiles.yaml`.

Tutti i parametri hardware stanno in `benchmarks/device_profiles.yaml`, ognuno con fonte e livello di
confidenza (`official` / `derived` / `assumption`).

**Validazione locale.** La GT 1030 (1.13 TFLOPS FP32, 48 GB/s) è una GPU di classe simile all'Adreno 650
del Quest 2 (1.2 TFLOPS, ~34–44 GB/s). Per lo stesso decoder (OPT-350M, pesi fp16) si confrontano:
- la latenza per token misurata col modello reale;
- quella del surrogato a 2 layer (mock);
- quella prevista dal modello roofline.

## 7. Limiti di validità

- **I numeri del visore sono stime.** Nessuno dei tre modelli è eseguibile su Horizon OS così com'è
  (vedi `requisiti_calcolo.md` §5). La stima ipotizza un porting ideale, cioè kernel GPU efficienti
  via Vulkan/OpenCL, e trascura che la GPU è contesa con il rendering.
- **L'efficienza η e l'overhead di dispatch sono ipotesi.** L'analisi di sensibilità (best/worst)
  ne delimita l'effetto.
- **Il numero di token per faccia dipende dalla forma.** I valori nelle specs vengono dai paper e
  vanno aggiornati con quelli misurati nei run completi (`mesh.tokens_per_face` nei report).
- **Le misure CPU locali risentono del paging** (8 GB di RAM condivisi con il desktop). Il report
  registra la RAM disponibile all'avvio (`system.ram_available_bytes_at_start`).
- **TreeMeshGPT e FastMesh campionano** (top-k / temperatura) e la lunghezza dell'output varia col seed:
  usare `--repeats` con seed diversi. MeshAnything V2 usa decoding greedy ed è deterministico.
