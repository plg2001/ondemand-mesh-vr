# Riepilogo benchmark

## Inferenza reale

| Modello | Device | Prec. | Shape | Punti | Stato | Inferenza [s] | ms/token (p50) | Token | Stima completa [s] | Picco VRAM [MB] | Picco RAM [MB] | Vertici | Triangoli | Chamfer-L2 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fastmesh V1K | cuda | fp16-full | bunny | 2048 | ok | 23.16 | 214.27 | 63.00 | 116.69 | 2458 | 7546 | 46.00 | 218.00 | — |
| meshanything default | cuda | fp16-full | bunny | 2048 | ok | 3.71 | 27.64 | 63.00 | 100.80 | 1281 | 4112 | 20.00 | 18.00 | — |
| treemeshgpt 7bit | cuda | fp16-full | bunny | 2048 | ok | 12.59 | 101.18 | 64.00 | 165.32 | 1926 | 6788 | 30.00 | 45.00 | — |

## Stima su visore standalone (roofline, fp16 nominale | int4 best case)

| Modello | Visore | Memoria richiesta [MB] | Budget app [MB] | Entra in memoria | Latenza [s] | Latenza int4 best [s] | Batteria/mesh [%] | Offload edge [s] |
|---|---|---|---|---|---|---|---|---|
| meshanything | quest2 | 1706 | 4506 | sì | 218.9 | 33.7 | 2.61 | 18.35 |
| meshanything | quest3s | 1706 | 5888 | sì | 146.6 | 28.7 | 1.70 | 18.35 |
| fastmesh | quest2 | 2559 | 4506 | sì | 26.8 | 4.6 | 0.32 | 2.56 |
| fastmesh | quest3s | 2559 | 5888 | sì | 17.8 | 3.6 | 0.21 | 2.56 |
| treemeshgpt | quest2 | 1590 | 4506 | sì | 131.0 | 21.8 | 1.56 | 8.77 |
| treemeshgpt | quest3s | 1590 | 5888 | sì | 86.9 | 17.6 | 1.01 | 8.77 |

