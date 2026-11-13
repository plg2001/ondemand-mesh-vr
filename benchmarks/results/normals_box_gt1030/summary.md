# Riepilogo benchmark

## Inferenza reale

Run *troncati*: 64 token AR per misurare i ms/token; la colonna *Stima completa* li estrapola a una mesh da 800 facce. Vertici, triangoli e qualità solo per generazioni complete.

*Normali*: quelle vere della mesh originale (*dalla mesh*) oppure stimate dai punti come fa il server per il visore, che manda solo i punti (*stimate*).

| Modello | Device | Prec. | Shape | Punti | Normali | Stato | Inferenza [s] | ms/token (p50) | Token | Stima completa [s] | Picco VRAM [MB] | Picco RAM privata [MB] | Vertici | Triangoli | Chamfer-L2 | F-score@1% |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| meshanything default | cuda | fp16-full | box | 2048 | stimate | ok (troncato) | 240.19 | 56.99 | 2812 | 205.93 | 1557 | 4112 | — | — | — | — |
| meshanything default | cuda | fp16-full | box | 2048 | dalla mesh | ok | 75.90 | 44.58 | 1596 | — | 1330 | 5033 | 314 | 336 | 0.00468 | 0.230 |

## Stima su visore standalone (roofline, fp16 nominale | int4 best case)

| Modello | Visore | Memoria richiesta [MB] | Budget app [MB] | Entra in memoria | Latenza [s] | Latenza int4 best [s] | Batteria/mesh [%] | Offload edge [s] |
|---|---|---|---|---|---|---|---|---|
| meshanything | quest2 | 1706 | 4506 | sì | 218.9 | 33.7 | 2.61 | 18.35 |
| meshanything | quest3s | 1706 | 5888 | sì | 146.6 | 28.7 | 1.70 | 18.35 |

