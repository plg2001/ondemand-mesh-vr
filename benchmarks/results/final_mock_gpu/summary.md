# Riepilogo benchmark

## Mock / profiling (senza pesi)

| Modello | Device | Punti | Parametri [M] | FLOPs/mesh [TFLOP] | Pesi fp16 [MB] | KV fp16 [MB] | Surrogato ms/token | Decoding stimato [s] |
|---|---|---|---|---|---|---|---|---|
| fastmesh V1K | cuda | 100 | 1099.1 | 0.78 | 2096 | 73 | 30.39 | 15.81 |
| fastmesh V1K | cuda | 2048 | 1099.1 | 0.78 | 2096 | 73 | 24.04 | 12.50 |
| fastmesh V1K | cuda | 500 | 1099.1 | 0.78 | 2096 | 73 | 25.49 | 13.26 |
| meshanything default | cuda | 100 | 500.8 | 3.12 | 955 | 362 | 29.24 | 105.28 |
| meshanything default | cuda | 2048 | 500.8 | 3.12 | 955 | 362 | 26.10 | 93.95 |
| meshanything default | cuda | 500 | 500.8 | 3.12 | 955 | 362 | 28.11 | 101.19 |
| treemeshgpt 7bit | cuda | 100 | 420.5 | 3.68 | 802 | 342 | 66.50 | 106.39 |
| treemeshgpt 7bit | cuda | 2048 | 420.5 | 3.68 | 802 | 342 | 69.29 | 110.86 |
| treemeshgpt 7bit | cuda | 500 | 420.5 | 3.68 | 802 | 342 | 70.14 | 112.23 |

## Stima su visore standalone (roofline, fp16 nominale | int4 best case)

| Modello | Visore | Memoria richiesta [MB] | Budget app [MB] | Entra in memoria | Latenza [s] | Latenza int4 best [s] | Batteria/mesh [%] | Offload edge [s] |
|---|---|---|---|---|---|---|---|---|
| meshanything | quest2 | 1706 | 4506 | sì | 218.9 | 33.7 | 2.61 | 18.35 |
| meshanything | quest3s | 1706 | 5888 | sì | 146.6 | 28.7 | 1.70 | 18.35 |
| fastmesh | quest2 | 2559 | 4506 | sì | 26.8 | 4.6 | 0.32 | 2.56 |
| fastmesh | quest3s | 2559 | 5888 | sì | 17.8 | 3.6 | 0.21 | 2.56 |
| treemeshgpt | quest2 | 1590 | 4506 | sì | 131.0 | 21.8 | 1.56 | 8.77 |
| treemeshgpt | quest3s | 1590 | 5888 | sì | 86.9 | 17.6 | 1.01 | 8.77 |

