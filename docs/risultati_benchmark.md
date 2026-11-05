# Riepilogo benchmark (Fase 1)

*Generato con `python -m benchmarks.summarize` dai report in `benchmarks/results/final_*` (macchina locale: i5-9400, 8 GB RAM, GeForce GT 1030 2 GB). Analisi e commento: [requisiti_calcolo.md](requisiti_calcolo.md).*

## Inferenza reale

Run *troncati*: 64 token AR per misurare i ms/token; la colonna *Stima completa* li estrapola a una mesh da 800 facce. Vertici, triangoli e qualità solo per generazioni complete.

| Modello | Device | Prec. | Shape | Punti | Stato | Inferenza [s] | ms/token (p50) | Token | Stima completa [s] | Picco VRAM [MB] | Picco RAM privata [MB] | Vertici | Triangoli | Chamfer-L2 | F-score@1% |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fastmesh V1K | cpu | fp32 | bunny | 100 | ok (troncato) | 29.35 | 128.41 | 63 | 73.94 | — | 5685 | — | — | — | — |
| fastmesh V1K | cpu | fp32 | bunny | 2048 | ok (troncato) | 26.32 | 122.09 | 63 | 67.66 | — | 5685 | — | — | — | — |
| fastmesh V1K | cpu | fp32 | bunny | 500 | ok (troncato) | 26.67 | 120.71 | 63 | 67.15 | — | 5686 | — | — | — | — |
| fastmesh V1K | cpu | fp32 | fastmesh_001 | 2048 | ok | 225.20 | 153.52 | 1255 | — | — | 6728 | 1112 | 5192 | 0.00041 | 0.473 |
| fastmesh V1K | cuda | fp16-full | bunny | 2048 | ok (troncato) | 23.16 | 214.27 | 63 | 116.69 | 2458 | 7546 | — | — | — | — |
| meshanything default | cpu | fp32 | box | 2048 | ok | 274.33 | 164.18 | 1596 | — | — | 3832 | 314 | 336 | 0.00468 | 0.230 |
| meshanything default | cpu | fp32 | bunny | 100 | ok (troncato) | 19.25 | 121.44 | 63 | 445.63 | — | 3399 | — | — | — | — |
| meshanything default | cpu | fp32 | bunny | 2048 | ok (troncato) | 16.25 | 131.04 | 63 | 476.46 | — | 3399 | — | — | — | — |
| meshanything default | cpu | fp32 | bunny | 500 | ok (troncato) | 15.34 | 125.56 | 63 | 456.78 | — | 3400 | — | — | — | — |
| meshanything default | cuda | fp16 | bunny | 2048 | ok (troncato) | 21.32 | 212.13 | 63 | 770.42 | 2931 | 5087 | — | — | — | — |
| meshanything default | cuda | fp16-full | box | 2048 | ok | 73.67 | 43.44 | 1596 | — | 1330 | 4110 | 314 | 336 | 0.00468 | 0.230 |
| meshanything default | cuda | fp16-full | bunny | 2048 | ok (troncato) | 3.71 | 27.64 | 63 | 100.80 | 1281 | 4112 | — | — | — | — |
| treemeshgpt 7bit | cpu | fp32 | bunny | 100 | ok (troncato) | 52.65 | 245.71 | 64 | 408.79 | — | 8438 | — | — | — | — |
| treemeshgpt 7bit | cpu | fp32 | bunny | 2048 | ok (troncato) | 49.40 | 243.30 | 64 | 403.29 | — | 8438 | — | — | — | — |
| treemeshgpt 7bit | cpu | fp32 | bunny | 500 | ok (troncato) | 48.39 | 250.74 | 64 | 415.26 | — | 8438 | — | — | — | — |
| treemeshgpt 7bit | cuda | fp16-full | bunny | 2048 | ok (troncato) | 12.59 | 101.18 | 64 | 165.32 | 1926 | 6788 | — | — | — | — |

## Mock / profiling (senza pesi)

| Modello | Device | Punti | Parametri [M] | FLOPs/mesh [TFLOP] | Pesi fp16 [MB] | KV fp16 [MB] | Surrogato ms/token | Decoding stimato [s] |
|---|---|---|---|---|---|---|---|---|
| fastmesh V1K | cpu | 100 | 1099.1 | 0.78 | 2096 | 73 | 120.68 | 62.76 |
| fastmesh V1K | cpu | 2048 | 1099.1 | 0.78 | 2096 | 73 | 120.26 | 62.54 |
| fastmesh V1K | cpu | 500 | 1099.1 | 0.78 | 2096 | 73 | 124.26 | 64.62 |
| fastmesh V1K | cuda | 100 | 1099.1 | 0.78 | 2096 | 73 | 30.39 | 15.81 |
| fastmesh V1K | cuda | 2048 | 1099.1 | 0.78 | 2096 | 73 | 24.04 | 12.50 |
| fastmesh V1K | cuda | 500 | 1099.1 | 0.78 | 2096 | 73 | 25.49 | 13.26 |
| meshanything default | cpu | 100 | 500.8 | 3.12 | 955 | 362 | 105.85 | 381.06 |
| meshanything default | cpu | 2048 | 500.8 | 3.12 | 955 | 362 | 128.10 | 461.16 |
| meshanything default | cpu | 500 | 500.8 | 3.12 | 955 | 362 | 125.57 | 452.04 |
| meshanything default | cuda | 100 | 500.8 | 3.12 | 955 | 362 | 29.24 | 105.28 |
| meshanything default | cuda | 2048 | 500.8 | 3.12 | 955 | 362 | 26.10 | 93.95 |
| meshanything default | cuda | 500 | 500.8 | 3.12 | 955 | 362 | 28.11 | 101.19 |
| treemeshgpt 7bit | cpu | 100 | 420.5 | 3.68 | 802 | 342 | 231.10 | 369.76 |
| treemeshgpt 7bit | cpu | 2048 | 420.5 | 3.68 | 802 | 342 | 260.82 | 417.32 |
| treemeshgpt 7bit | cpu | 500 | 420.5 | 3.68 | 802 | 342 | 241.39 | 386.23 |
| treemeshgpt 7bit | cuda | 100 | 420.5 | 3.68 | 802 | 342 | 66.50 | 106.39 |
| treemeshgpt 7bit | cuda | 2048 | 420.5 | 3.68 | 802 | 342 | 69.29 | 110.86 |
| treemeshgpt 7bit | cuda | 500 | 420.5 | 3.68 | 802 | 342 | 70.14 | 112.23 |

## Stima su visore standalone (roofline, fp16 nominale | int4 best case)

| Modello | Visore | Memoria richiesta [MB] | Budget app [MB] | Entra in memoria | Latenza [s] | Latenza int4 best [s] | Batteria/mesh [%] | Offload edge [s] |
|---|---|---|---|---|---|---|---|---|
| fastmesh | quest2 | 2559 | 4506 | sì | 26.8 | 4.6 | 0.32 | 2.56 |
| fastmesh | quest3s | 2559 | 5888 | sì | 17.8 | 3.6 | 0.21 | 2.56 |
| meshanything | quest2 | 1706 | 4506 | sì | 218.9 | 33.7 | 2.61 | 18.35 |
| meshanything | quest3s | 1706 | 5888 | sì | 146.6 | 28.7 | 1.70 | 18.35 |
| treemeshgpt | quest2 | 1590 | 4506 | sì | 131.0 | 21.8 | 1.56 | 8.77 |
| treemeshgpt | quest3s | 1590 | 5888 | sì | 86.9 | 17.6 | 1.01 | 8.77 |

