# Analisi dei requisiti di calcolo — inferenza point cloud → mesh su visore standalone

*Fase 1 della tesi. Le tabelle delle sezioni 3–5 si rigenerano con
`python -m benchmarks.requirements_tables`; le misure locali della sezione 4.1 con
`python -m benchmarks.run_benchmarks` (vedi [metodologia](metodologia_benchmark.md)).*

## 0. Sintesi

1. **Nessuno dei tre modelli è eseguibile sul visore così com'è.**
   - Il codice dipende dallo stack CUDA/PyTorch desktop: MeshAnything V2 e FastMesh hanno
     FlashAttention-2 cablato nel codice.
   - Le dipendenze geometriche sono Python desktop (open3d, trimesh, networkx).
   - Horizon OS (Android arm64, GPU Adreno senza CUDA) non offre nessuno di questi runtime.
2. **Anche con un porting ideale il costo è proibitivo.** La generazione è autoregressiva e, a batch 1,
   limitata dalla banda di memoria: ogni token rilegge tutti i pesi del decoder (~0,63 GB in fp16 per
   OPT-350M) più la KV cache.
   - Per una mesh da 800 facce MeshAnything V2 genera circa 3 600 token. Sono circa 3 TB di traffico
     DRAM per mesh.
   - Sulla LPDDR4X del Quest 2 (~34 GB/s) servono circa **3,5 minuti** per mesh in fp16 (TreeMeshGPT
     ~2 min, FastMesh ~27 s). Il caso migliore int4 resta da qualche secondo (FastMesh, ~5 s) a decine di
     secondi (MA V2 ~34 s, TreeMeshGPT ~22 s).
   - Un frame a 90 Hz dura 11,1 ms.
3. **La memoria non è il limite più duro in fp16, ma resta incompatibile con un'applicazione reale.**
   - I modelli occupano il 35–57% del budget di memoria per app del Quest 2 (4,4 GiB), che deve
     contenere anche la scena VR.
   - In fp32 (esecuzione PyTorch diretta) FastMesh supera il budget.
   - TreeMeshGPT alla lunghezza massima (32k token) richiede ~2,9 GiB di sola KV cache in fp16.
4. **L'energia per mesh è dell'1–6% della batteria con MeshAnything V2** (0,2–0,6% con FastMesh),
   in competizione con il rendering e soggetta a throttling termico.
5. **L'offloading su un nodo Edge con GPU moderna riduce la latenza di circa un ordine di grandezza**
   (stima: 10–15×).
   - La rete pesa pochissimo: la nuvola sparsa vale pochi KB, il GLB decine di KB.
   - Il visore resta libero di renderizzare.
   - Anche sull'Edge, però, la generazione AR richiede **secondi**: la Fase 2 dovrà prevedere il
     frustum (prefetch), mettere in cache le mesh generate e preferire architetture a pochi token
     (FastMesh).
6. **La densità della nuvola sparsa non riduce il costo.** Tutti gli encoder ricampionano l'input a
   8192 punti (4096 per TreeMeshGPT 9bit): il costo dipende dalle facce generate. La densità influisce
   solo sulla qualità.
   - Misurato: con 100, 500 o 2048 punti la latenza per token cambia di ±4%.
7. **Evidenza empirica locale** (§4.1).
   - Sulla GT 1030, una GPU di classe di banda simile all'Adreno 650:
     - MeshAnything V2 genera ~23 token/s e completa una mesh semplice (box, 336 facce) in **74 s**;
     - su una forma più complessa non termina in **25 minuti**.
   - Su CPU desktop FastMesh produce una mesh completa (1 112 vertici) in **225 s**.
   - Il modello roofline usato per il visore prevede i ms/token misurati entro il 20% a contesto breve,
     quando il modello sta in memoria. A contesto lungo la KV cache dinamica di HF porta lo scarto fino
     a ~1,7× (§4.1), quindi le stime sul visore sono ottimistiche. Fuori dalla VRAM il rallentamento è
     di 3–9×.

## 1. Il problema rispetto al paradigma "scene graph"

Il paradigma della tesi sostituisce lo scene graph (BVH/octree, attraversato a ogni frame per il
frustum/occlusion culling) con nuvole di punti sparse, da cui la mesh si genera *on-demand*
quando l'oggetto entra nel campo visivo.
- La nuvola sparsa è minuscola: 500 punti xyz in float32 occupano 5,9 KB; 2048 punti, 24 KB.
- Il generatore neurale però sposta il costo dalla traversata dell'albero all'inferenza.
- La decodifica autoregressiva è **anch'essa memory-bound**, ed è proprio la risorsa (banda/memoria
  del SoC) che il paradigma voleva liberare.
- Per gli stessi oggetti, il traffico DRAM per generare una mesh supera di molti ordini di grandezza
  quello di una traversata BVH. Da qui la necessità di spostare la generazione fuori dal visore.

## 2. Modelli e caratteristiche computazionali

Dati letti dai sorgenti ufficiali (commit in `models/upstream.lock.json`). Parametri **esatti**:
architettura ufficiale istanziata su device meta.

| | MeshAnything V2 | TreeMeshGPT (7bit) | FastMesh (V1K) |
|---|---|---|---|
| Licenza | S-Lab 1.0 (non commerciale) | MIT | S-Lab 1.0 (non commerciale) |
| Parametri totali | 500,8 M | 420,5 M | 1 099,1 M |
| Encoder point cloud | Michelangelo perceiver 185 M (di cui 16 layer di decoder VAE inutilizzati in inferenza) | CloudEncoder 5,3 M, 2048 query | Michelangelo 177 M (+ refiner 255 M) |
| Decoder AR | OPT-350M (24 L, d=1024), 313 M | 24 L, d=1024, GLU, 403 M + teste 9,8 M | OPT-350M, 314 M |
| Componenti one-shot | — | — | VertRefine 6 L; FaceGen 24 L bidirezionale (adiacenza V×V), 350 M |
| Input | 8192 punti + normali | 8192 punti (no normali) | 8192 punti + normali |
| Token di condizione | 257 | 2048 (prefisso bidirezionale) | 257 |
| Token AR per faccia | ~4,5 (AMT; max 6,3) | ~2 (tree sequencing) | ~0,65 con V ≈ F/2 (1 token per vertice + token di blocco; misurato: 1,13 token/vertice) |
| Facce max | 1 600 | 5 500 (9bit: 11 000) | 1 512 (V4K: 4 512) |
| Token AR max | 10 083 | 30 000 | 1 514 |
| Attenzione | FA2 obbligatoria (`ValueError` altrimenti) | SDPA | FA2 obbligatoria (VertGen e FaceGen) |

## 3. Memoria

<!-- generato da: python -m benchmarks.requirements_tables -->
### 3.1 Pesi e KV cache

| Modello | Parametri | Pesi fp32 | Pesi fp16 | Pesi int8 | Pesi int4 | KV/token fp16 | Token max | KV a lunghezza max (fp16) |
|---|---|---|---|---|---|---|---|---|
| MeshAnything V2 | 501 M | 1.87 GiB | 0.93 GiB | 0.47 GiB | 0.23 GiB | 96 KiB | 10340 | 0.95 GiB |
| TreeMeshGPT 7bit | 421 M | 1.57 GiB | 0.78 GiB | 0.39 GiB | 0.20 GiB | 96 KiB | 32048 | 2.93 GiB |
| FastMesh V1K | 1099 M | 4.09 GiB | 2.05 GiB | 1.02 GiB | 0.51 GiB | 96 KiB | 1771 | 0.16 GiB |

KV cache per token = 2 (K, V) × 24 layer × 1024 × 2 byte = 96 KiB in fp16, per tutti e tre i decoder.

### 3.2 Confronto con il budget di memoria per app

Meta documenta un limite PSS per app di **4,4 GiB su Quest 2** e **5,75 GiB su Quest 3/3S**
(Horizon OS developer docs, "Memory / RAM"). Il resto della RAM fisica (6 / 8 GB) è riservato al
sistema.

| Modello | Precisione | 800 facce | Lunghezza massima | Quota budget Quest 2 (800 f.) | Entra in Quest 2 (max) | Entra in Quest 3S (max) |
|---|---|---|---|---|---|---|
| MeshAnything V2 | fp32 | 2.96 GiB | 4.15 GiB | 67% | sì | sì |
| MeshAnything V2 | fp16 | 1.67 GiB | 2.26 GiB | 38% | sì | sì |
| MeshAnything V2 | int4 | 0.97 GiB | 1.56 GiB | 22% | sì | sì |
| TreeMeshGPT 7bit | fp32 | 2.73 GiB | 7.93 GiB | 62% | **no** | **no** |
| TreeMeshGPT 7bit | fp16 | 1.55 GiB | 4.15 GiB | 35% | sì | sì |
| TreeMeshGPT 7bit | int4 | 0.96 GiB | 3.56 GiB | 22% | sì | sì |
| FastMesh V1K | fp32 | 4.63 GiB | 4.81 GiB | 105% | **no** | sì |
| FastMesh V1K | fp16 | 2.50 GiB | 2.59 GiB | 57% | sì | sì |
| FastMesh V1K | int4 | 0.96 GiB | 1.05 GiB | 22% | sì | sì |

Lettura:
- Il budget è **per l'intera applicazione**: geometria, texture, framebuffer, audio e logica della
  scena VR devono starci insieme al modello. Un generatore che ne occupa da un terzo a più della metà
  annulla il vantaggio di memoria che il paradigma "solo nuvole sparse" voleva ottenere.
- Eseguire PyTorch in fp32 (nessuna conversione) supera il budget del Quest 2 con FastMesh.
- TreeMeshGPT alla lunghezza massima supera il budget anche su Quest 3S.
- Le versioni int4 ci starebbero, ma per modelli che predicono *coordinate quantizzate* (7–9 bit) la
  quantizzazione aggressiva dei pesi è un rischio non verificato per la correttezza geometrica.

### 3.3 Evidenza empirica (GPU locale da 2 GB)

Su GeForce GT 1030 (2 GB, di cui ~0,9 GB già usati dal desktop) MeshAnything V2 con la configurazione
upstream (pesi fp32 + autocast fp16) arriva a un picco di **2,93 GB allocati**, oltre la VRAM fisica.
I soli pesi occupano 1,91 GB; il report segnala 930 MB di spill
(`final_real_gt1030_trunc/runs/real_meshanything_bunny_n2048_fp16-autocast.json`).
- Su Windows il driver (WDDM, *CUDA Sysmem Fallback Policy*) non solleva OOM: sposta le allocazioni
  nella RAM di sistema attraverso il PCIe.
- Il run termina, ma a **212 ms/token**, contro i **29 ms/token** con pesi fp16 che stanno in VRAM:
  7× più lento.
- Con il fallback disabilitato, o su Linux, lo stesso run termina con OOM.
- È lo stesso tipo di vincolo che sul visore porta al *Low Memory Kill* dell'app.

## 4. Latenza

### 4.1 Misure locali (codice e pesi ufficiali)

Macchina di sviluppo:
- Intel i5-9400 (6 core, AVX2), 8 GB DDR4-2666 single channel, con circa 1–2,5 GB liberi durante le misure;
- NVIDIA GeForce GT 1030, 2 GB GDDR5, Pascal sm_61;
- Windows 11, PyTorch 2.7.1+cu118, transformers 4.43.0, attenzione con shim SDPA.

Input: Stanford Bunny a 2048 punti, ricampionato a 8192. Run troncati a 64 token AR per misurare i
ms/token a regime. Report completi in `benchmarks/results/final_*`.

**GPU GT 1030** (pesi fp16, `--precision fp16-full`). La GT 1030 ha una classe di banda e di calcolo
simile all'Adreno 650 del Quest 2: 48 contro ~34–44 GB/s, 1,1 contro 1,2 TFLOPS.

| Modello | ms/token (p50) | Encoder | Prefill | Refine + FaceGen | Picco VRAM | Oltre la VRAM fisica | Roofline prevista | Misurato / previsto |
|---|---|---|---|---|---|---|---|---|
| MeshAnything V2 | **27,6** | 0,61 s | 0,67 s | — | 1 281 MB | no | 23,0 ms (memory-bound) | 1,20 |
| TreeMeshGPT 7bit | 101,2 | 0,22 s | 3,21 s | — | 1 926 MB | sì | 36,1 ms | 2,81 |
| FastMesh V1K | 214,3 | 1,46 s | 3,81 s | 0,75 s + 2,29 s | 2 458 MB | sì | 23,1 ms | 9,29 |

Lettura:
- Per il modello che sta in VRAM (MeshAnything V2) il modello roofline prevede la latenza per token
  entro il 20%. È la base quantitativa per le stime sul visore.
- TreeMeshGPT (prefisso di 2048 token + KV) e FastMesh (2,2 GB di pesi fp16) superano i ~1,1 GB liberi.
  Su Windows la memoria deborda nella RAM di sistema e il decoding rallenta di 3–9×. Su un SoC mobile
  la stessa condizione è un *Low Memory Kill*.
- **Generazione completa.** MeshAnything V2 (input `mav2_wand`, 2048 punti, fino a 10 083 token)
  **non è terminato entro 25 minuti** sulla GT 1030. Con l'aumentare della KV cache l'occupazione supera
  la VRAM e ogni token diventa più lento.

**CPU i5-9400** (pesi fp32): sweep su 100 / 500 / 2048 punti di input, 64 token.

| Modello | ms/token (p50) per 100 / 500 / 2048 punti | Encoder | Prefill | Refine + FaceGen | Picco working set | Roofline prevista | Misurato / previsto | Stima generazione completa (800 facce) |
|---|---|---|---|---|---|---|---|---|
| MeshAnything V2 | 121 / 126 / 131 | 1,3–1,4 s | 3,4–7,1 s | — | 2,0–2,2 GB | 89,8 ms | 1,35–1,46 | **~7,5–8 min** (3 600 token) |
| TreeMeshGPT 7bit | 246 / 251 / 243 | 0,5–0,6 s | 13,5–15,1 s | — | 2,6 GB | 140,2 ms | 1,74–1,79 | **~6,7–6,9 min** (1 600 token) |
| FastMesh V1K | 128 / 121 / 122 | 1,4 s | 2,8–5,7 s | 3,4–4,4 s + 7,2–8,8 s | 3,0–3,3 GB | 89,9 ms | 1,34–1,43 | **~67–74 s** (520 token) |

Osservazioni:
1. **Il numero di punti in input non cambia il costo.** Tra 100 e 2048 punti la latenza per token
   varia solo per rumore di misura (±4%), perché l'input viene sempre ricampionato a 8192 punti.
   Il costo dipende dal numero di token generati.
2. **Il decoding è memory-bound anche su CPU.** Il modello roofline, con la sola banda DDR4 single
   channel di 21,3 GB/s, sottostima di 1,35–1,8×. La parte mancante è overhead di dispatch eager di
   PyTorch e, per TreeMeshGPT, la logica Python per step (stack dell'albero, controllo delle facce
   duplicate, tre teste MLP).
3. **TreeMeshGPT paga un prefill costoso**: 2048 token di condizione bidirezionali, 13–15 s su CPU.
4. **Picchi di memoria privata committed** (Windows): MA ~3,4 GB, FastMesh ~5,7 GB, TreeMeshGPT
   ~8,4 GB.
   - Per TreeMeshGPT il dato include la mappatura copy-on-write del checkpoint da 5 GB caricato in
     mmap: è memoria impegnata, non necessariamente residente.
   - In ogni caso sono valori fuori dalla portata di un'app Quest (budget 4,4 / 5,75 GiB) se eseguiti
     così come sono.

**Validazione del metodo mock** (surrogato a 2 layer con pesi casuali, scalato alla profondità piena):

| Modello | Surrogato CPU | Reale CPU | Surrogato GT 1030 | Reale GT 1030 |
|---|---|---|---|---|
| MeshAnything V2 | 106–128 ms | 121–131 ms | 26–29 ms | 27,6 ms |
| TreeMeshGPT 7bit | 231–261 ms | 243–251 ms | 67–70 ms | 101 ms* |
| FastMesh V1K | 120–124 ms | 121–128 ms | 24–30 ms | 214 ms* |

\* Run reali oltre la VRAM fisica (spill nella RAM di sistema): il surrogato, che sta in VRAM, misura
il caso senza spill.

Il surrogato riproduce i ms/token reali entro il ±15% quando non c'è spill. La modalità mock è
quindi uno strumento affidabile per stimare il costo su hardware dove i pesi completi non entrano.

**Generazioni complete** (con export della mesh e metriche di qualità rispetto alla nuvola densa di
riferimento):

| Run | Tempo totale | Decoding | Altri stadi | Token | Mesh | Qualità |
|---|---|---|---|---|---|---|
| FastMesh V1K, CPU, `fastmesh_001` a 2048 punti | **225 s** | 196,6 s (p50 154 ms/token, 132→175 ms con il contesto) | encoder 1,4 s, prefill 5,3 s, refine 4,7 s, FaceGen 11,1 s | 1 255 (1,13 per vertice) | 1 112 vertici, 5 192 triangoli, GLB da 75 KB | Chamfer-L1 0,024, F-score@1% 0,47 |
| MeshAnything V2, GT 1030 fp16, `box` a 2048 punti | **73,7 s** | 69,3 s (p50 43 ms/token, 29→58 ms con il contesto) | encoder 0,7 s, prefill 0,5 s, detokenize 0,5 s | 1 596 (**4,75 per faccia**) | 314 vertici, 336 triangoli, GLB da 8,4 KB | Chamfer-L1 0,065, F-score@1% 0,23 |
| MeshAnything V2, CPU fp32, `box` a 2048 punti | **274,3 s** | p50 164 ms/token (123→190 ms) | — | 1 596 | identica al run GPU: 314 v / 336 f | identica |
| MeshAnything V2, GT 1030 fp16, `mav2_wand` a 2048 punti | **> 25 min** (non terminato) | — | — | fino a 10 083 | — | — |

Il confronto CPU fp32 / GPU fp16 verifica la correttezza della pipeline. Con decoding greedy, lo shim
SDPA e i pesi fp16 producono **la stessa identica sequenza di token** del calcolo in fp32: stessa
mesh, stesse metriche.

Osservazioni sulle generazioni complete:
- **I token per faccia misurati confermano le specs.** Per MeshAnything V2 risultano 4,75, contro
  ~4,5 dichiarati per la tokenizzazione AMT. Per FastMesh sono 1,13 per vertice.
- **Nel decoding completo la latenza per token raddoppia** dall'inizio alla fine della sequenza
  (MA: 29→58 ms; FastMesh su CPU: 132→175 ms). Il modello roofline, che conta solo la lettura di pesi
  e KV cache, prevede +24%. La differenza viene dalla KV cache dinamica di Hugging Face, che a ogni
  step concatena (`torch.cat`) e quindi ricopia l'intera cache di ogni layer: traffico O(L) aggiuntivo
  per token.
  - Una KV cache statica preallocata, obbligatoria comunque per un porting mobile, elimina questo
    costo ma fissa la memoria alla lunghezza massima (§3).
- Anche a parità di modello, il tempo dipende fortemente dalla forma: il box si chiude in 1 596 token,
  la bacchetta (`mav2_wand`) non termina in 25 minuti. **La latenza di generazione non è prevedibile
  a priori**, un aspetto critico per un sistema real-time.
- Le mesh in uscita sono riportate nel frame della nuvola di input:
  - **FastMesh** ricostruisce correttamente la forma (F-score 0,47 a 1% della diagonale);
  - **MeshAnything V2** sul box produce una mesh "artist-like" corretta nell'ingombro, ma con 336
    triangoli in parte sovrapposti o interni, mentre un box ne richiede 12.

### 4.2 Stima roofline sul visore

| Modello | Facce | Token AR | FLOPs | Quest 2 [s] | Quest 3S [s] | Batteria Quest 2 / mesh | Mesh per carica (Q2) | Edge RTX 4090 + Wi-Fi 6 [s] |
|---|---|---|---|---|---|---|---|---|
| MeshAnything V2 | 400 | 1800 | 1.51 T | 99 [15–149] | 66 [13–116] | 1.17% | 85 | 9.0 |
| MeshAnything V2 | 800 | 3600 | 3.12 T | 219 [34–328] | 147 [29–255] | 2.61% | 38 | 18.4 |
| MeshAnything V2 | 1500 | 6750 | 6.71 T | 483 [74–717] | 323 [63–557] | 5.75% | 17 | 35.8 |
| TreeMeshGPT 7bit | 400 | 800 | 2.76 T | 65 [12–96] | 43 [9–73] | 0.77% | 130 | 4.4 |
| TreeMeshGPT 7bit | 800 | 1600 | 3.68 T | 131 [22–193] | 87 [18–149] | 1.56% | 64 | 8.8 |
| TreeMeshGPT 7bit | 1500 | 3000 | 5.43 T | 258 [41–379] | 171 [34–293] | 3.07% | 33 | 16.7 |
| FastMesh V1K | 400 | 260 | 0.49 T | 13 [2–20] | 9 [2–16] | 0.16% | 628 | 1.3 |
| FastMesh V1K | 800 | 520 | 0.78 T | 27 [5–41] | 18 [4–31] | 0.32% | 313 | 2.6 |
| FastMesh V1K | 1500 | 975 | 1.31 T | 52 [9–78] | 34 [7–60] | 0.61% | 163 | 4.8 |

Nominale fp16; tra parentesi l'intervallo [best case int4, worst case fp16]. Ipotesi di
`device_profiles.yaml`:
- Adreno 650 a 1,2 TFLOPS FP32 e LPDDR4X a 34 GB/s;
- XR2 Gen 2 a 2,5× la GPU del Gen 1 e LPDDR5 a 51 GB/s;
- η_memoria = 0,6, η_calcolo = 0,35, fattore termico sostenuto 0,7.

**Perché è memory-bound.**
- Uno step di MeshAnything V2 con contesto ~2 000 costa ~0,8 GFLOP ma legge ~0,8 GB.
- L'intensità aritmetica, circa 1 FLOP/byte, è molto sotto il punto di cresta dell'Adreno 650
  (~35 FLOP/byte).
- Raddoppiare i TFLOPS non serve; dimezzare i byte (int8/int4) sì. È il motivo per cui il caso
  migliore assume pesi a 4 bit.

**Vincolo di frame.** A 90 Hz il budget per frame è di 11,1 ms. Generare una sola mesh equivale a
1 000–45 000 frame. Inoltre la GPU del visore è condivisa con il compositor: un carico di calcolo di
decine di secondi provoca cali di frame rate (reprojection) o va eseguito a bassa priorità,
allungando ulteriormente i tempi.

## 5. Energia e termica

L'energia stimata è E = P_SoC × t, con P_SoC sostenuta di 6 W su Quest 2 e 7 W su Quest 3S
(ipotesi, intervallo 4–9 W):
- MeshAnything V2: circa 2,6% della batteria del Quest 2 (14 Wh) per una mesh da 800 facce, cioè
  ~38 mesh per carica, *senza* contare il rendering;
- FastMesh: circa 0,3%, ~310 mesh per carica.

A questo si aggiunge il throttling termico: Horizon OS fissa i livelli di clock CPU/GPU e un carico
sostenuto di minuti abbassa le prestazioni (fattore 0,5–0,9 nell'analisi di sensibilità). Con
l'offloading, il costo energetico per il visore si riduce alla trasmissione radio: pochi KB in
upload e decine di KB in download, meno di 10 mJ per mesh con Wi-Fi 6.

## 6. Incompatibilità architetturali con ARM64 / Horizon OS

| Ostacolo | Dove | Effetto sul visore |
|---|---|---|
| Nessun CUDA su Adreno | tutto lo stack PyTorch-CUDA | Il backend GPU di PyTorch non esiste su Horizon OS. Restano CPU arm64, Vulkan/OpenCL o runtime dedicati (Unity Inference Engine/Sentis, ExecuTorch, ONNX Runtime). |
| FlashAttention-2 cablato | `shape_opt.py` (MA V2, FastMesh), `SelfAttention_Flash` di FaceGen | `flash_attn` è solo CUDA sm80+ (Ampere/Hopper). Senza modifiche il codice solleva `ValueError`. Lo shim SDPA di questo repo funziona solo dove esiste PyTorch. |
| Nessun runtime Python nelle app | wrapper, pipeline upstream | Le app Quest sono APK nativi (Unity/Unreal/OpenXR, C#/C++). Tutta la pipeline va riscritta. |
| Controllo di flusso dipendente dai dati in Python | MA: `OPTLoopEmbedding`, `adjacent_detokenize` (loop per token); TMG: stack dell'albero, rifiuto delle facce duplicate con ricampionamento; FastMesh: post-processing a grafo con networkx | Non esportabile in un grafo statico (ONNX/TFLite) nel suo insieme. Si esporta solo lo step del transformer; la logica va portata in C#/C++. |
| `generate()` di HF con KV cache dinamica | MA V2, FastMesh | I compilatori mobile/NPU richiedono shape statiche: la KV cache va preallocata alla lunghezza massima (MA 10 k, TMG 32 k token), con il costo di memoria della §3. |
| Precisione e quantizzazione | tutti | Addestrati e validati in fp16 autocast. La quantizzazione int8/int4 di modelli che predicono coordinate quantizzate non è verificata. Sulle GPU mobile il formato di riferimento è fp16; il supporto bf16 va verificato per il runtime scelto. |
| NPU Hexagon | XR2 Gen 1/Gen 2 | Non risulta un accesso documentato per le app Quest di terze parti, e l'AR a batch 1 è comunque limitato dalla banda DRAM condivisa. |
| GPU condivisa con il rendering | tutti | Il compositor ha priorità: l'inferenza compete con il frame budget di 11 ms. |
| Dipendenze geometriche desktop | open3d, trimesh, scipy, networkx, scikit-image, opencv | Nessuna build Android Python ufficiale: vanno sostituite. |
| Peso dei checkpoint | 2,0 / 4,4 / 5,0 GB su disco (MA / FM / TMG) | Download e storage notevoli per un APK, vanno convertiti e quantizzati. |
| Licenze | MA V2, FastMesh: S-Lab 1.0 | Uso non commerciale: compatibile con la tesi, non con una distribuzione su store. |

## 7. Conclusione: perché l'offloading su Edge

- Sul visore, anche ipotizzando un porting nativo ideale, la generazione di una mesh richiede da
  decine di secondi a minuti, consuma una frazione importante del budget di memoria dell'app e
  qualche punto percentuale di batteria per oggetto.
- Su un nodo Edge con GPU moderna lo stesso lavoro richiede secondi (stima roofline calibrata, da
  misurare sul nodo reale con `run_benchmarks --device cuda`).
  - Il visore spedisce pochi KB di nuvola e riceve qualche decina di KB di GLB.
  - Con Wi-Fi 6 la rete è trascurabile, < 10 ms.
- La latenza residua sull'Edge, che resta di secondi, è il dato di progetto per la Fase 2:
  - generare le mesh **prima** che entrino nel frustum (predizione del movimento della testa e prefetch);
  - **cache** sull'Edge e sul visore delle mesh già generate;
  - priorità per distanza/salienza;
  - preferenza per architetture a pochi token: secondo le specs, FastMesh genera circa 7× meno token
    di MeshAnything V2 per la stessa mesh; il rapporto va confermato con i run completi sull'Edge.
