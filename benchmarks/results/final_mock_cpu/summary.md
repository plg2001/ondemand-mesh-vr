# Riepilogo benchmark

## Mock / profiling (senza pesi)

| Modello | Device | Punti | Parametri [M] | FLOPs/mesh [TFLOP] | Pesi fp16 [MB] | KV fp16 [MB] | Surrogato ms/token | Decoding stimato [s] |
|---|---|---|---|---|---|---|---|---|
| fastmesh V1K | cpu | 100 | 1099.1 | 0.78 | 2096 | 73 | 120.68 | 62.76 |
| fastmesh V1K | cpu | 2048 | 1099.1 | 0.78 | 2096 | 73 | 120.26 | 62.54 |
| fastmesh V1K | cpu | 500 | 1099.1 | 0.78 | 2096 | 73 | 124.26 | 64.62 |
| meshanything default | cpu | 100 | 500.8 | 3.12 | 955 | 362 | 105.85 | 381.06 |
| meshanything default | cpu | 2048 | 500.8 | 3.12 | 955 | 362 | 128.10 | 461.16 |
| meshanything default | cpu | 500 | 500.8 | 3.12 | 955 | 362 | 125.57 | 452.04 |
| treemeshgpt 7bit | cpu | 100 | 420.5 | 3.68 | 802 | 342 | 231.10 | 369.76 |
| treemeshgpt 7bit | cpu | 2048 | 420.5 | 3.68 | 802 | 342 | 260.82 | 417.32 |
| treemeshgpt 7bit | cpu | 500 | 420.5 | 3.68 | 802 | 342 | 241.39 | 386.23 |

## Stima su visore standalone (roofline, fp16 nominale | int4 best case)

| Modello | Visore | Memoria richiesta [MB] | Budget app [MB] | Entra in memoria | Latenza [s] | Latenza int4 best [s] | Batteria/mesh [%] | Offload edge [s] |
|---|---|---|---|---|---|---|---|---|
| meshanything | quest2 | 1706 | 4506 | sì | 218.9 | 33.7 | 2.61 | 18.35 |
| meshanything | quest3s | 1706 | 5888 | sì | 146.6 | 28.7 | 1.70 | 18.35 |
| fastmesh | quest2 | 2559 | 4506 | sì | 26.8 | 4.6 | 0.32 | 2.56 |
| fastmesh | quest3s | 2559 | 5888 | sì | 17.8 | 3.6 | 0.21 | 2.56 |
| treemeshgpt | quest2 | 1590 | 4506 | sì | 131.0 | 21.8 | 1.56 | 8.77 |
| treemeshgpt | quest3s | 1590 | 5888 | sì | 86.9 | 17.6 | 1.01 | 8.77 |

