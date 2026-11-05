# Riepilogo benchmark

## Inferenza reale

| Modello | Device | Prec. | Shape | Punti | Stato | Inferenza [s] | ms/token (p50) | Token | Stima completa [s] | Picco VRAM [MB] | Picco RAM [MB] | Vertici | Triangoli | Chamfer-L2 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fastmesh V1K | cpu | fp32 | bunny | 100 | ok | 29.35 | 128.41 | 63.00 | 73.94 | — | 5685 | 46.00 | 114.00 | — |
| fastmesh V1K | cpu | fp32 | bunny | 2048 | ok | 26.32 | 122.09 | 63.00 | 67.66 | — | 5685 | 44.00 | 190.00 | — |
| fastmesh V1K | cpu | fp32 | bunny | 500 | ok | 26.67 | 120.71 | 63.00 | 67.15 | — | 5686 | 51.00 | 309.00 | — |
| meshanything default | cpu | fp32 | bunny | 100 | ok | 19.25 | 121.44 | 63.00 | 445.63 | — | 3399 | 16.00 | 11.00 | — |
| meshanything default | cpu | fp32 | bunny | 2048 | ok | 16.25 | 131.04 | 63.00 | 476.46 | — | 3399 | 20.00 | 18.00 | — |
| meshanything default | cpu | fp32 | bunny | 500 | ok | 15.34 | 125.56 | 63.00 | 456.78 | — | 3400 | 20.00 | 18.00 | — |
| treemeshgpt 7bit | cpu | fp32 | bunny | 100 | ok | 52.65 | 245.71 | 64.00 | 408.79 | — | 8438 | 32.00 | 46.00 | — |
| treemeshgpt 7bit | cpu | fp32 | bunny | 2048 | ok | 49.40 | 243.30 | 64.00 | 403.29 | — | 8438 | 32.00 | 46.00 | — |
| treemeshgpt 7bit | cpu | fp32 | bunny | 500 | ok | 48.39 | 250.74 | 64.00 | 415.26 | — | 8438 | 30.00 | 45.00 | — |

## Stima su visore standalone (roofline, fp16 nominale | int4 best case)

| Modello | Visore | Memoria richiesta [MB] | Budget app [MB] | Entra in memoria | Latenza [s] | Latenza int4 best [s] | Batteria/mesh [%] | Offload edge [s] |
|---|---|---|---|---|---|---|---|---|
| meshanything | quest2 | 1706 | 4506 | sì | 218.9 | 33.7 | 2.61 | 18.35 |
| meshanything | quest3s | 1706 | 5888 | sì | 146.6 | 28.7 | 1.70 | 18.35 |
| fastmesh | quest2 | 2559 | 4506 | sì | 26.8 | 4.6 | 0.32 | 2.56 |
| fastmesh | quest3s | 2559 | 5888 | sì | 17.8 | 3.6 | 0.21 | 2.56 |
| treemeshgpt | quest2 | 1590 | 4506 | sì | 131.0 | 21.8 | 1.56 | 8.77 |
| treemeshgpt | quest3s | 1590 | 5888 | sì | 86.9 | 17.6 | 1.01 | 8.77 |

