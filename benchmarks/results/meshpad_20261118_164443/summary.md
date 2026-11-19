# MeshPad su schizzi (2026-11-18 16:44)

- Dispositivo: decoder **cuda** (NVIDIA GeForce GT 1030), encoder RADIO **cpu**, precisione fp16-full, attenzione sdpa-shim
- Caricamento del modello: 6.3 s (picco RAM 3.86 GB)
- Vista degli schizzi: azimut 45°, elevazione 30°; generazione campionata come nella demo (top-k 10, top-p 0,8, temperatura 0,5); limite 180 s
- Accuratezza rispetto alla nuvola densa della mesh originale (F-score all'1% della diagonale, Chamfer-L2), sia com'è uscita sia ruotata nel modo migliore attorno all'asse verticale (MeshPad sceglie da solo il davanti di un oggetto).

| Forma | Seme | Esito | Triangoli | Token | Passi decoder | Encoder [s] | Generazione [s] | ms/passo (p50) | Totale [s] | VRAM picco [MiB] | F-score com'è | F-score ruotata | rotazione | Chamfer-L2 ruotata |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| scatola | 0 | ok | 28 | 132 | 49 | 2.04 | 3.5 | 62 | 7.0 | 756 | 0.67 | 0.67 | 0° | 0.00393 |
| scatola | 1 | ok | 20 | 122 | 47 | 1.87 | 2.8 | 62 | 5.0 | 754 | 0.72 | 0.72 | 180° | 0.00023 |
| scatola | 2 | ok | 37 | 211 | 78 | 1.83 | 4.8 | 62 | 7.0 | 771 | 0.75 | 0.75 | 0° | 0.00017 |
| coniglio | 0 | timeout | – | – | 1494 | 2.46 | 169.6 | 95 | 180.1 | 1449 | – | – | –° | – |
| coniglio | 1 | timeout | – | – | 1418 | 3.20 | 169.0 | 102 | 180.1 | 1456 | – | – | –° | – |
| coniglio | 2 | timeout | – | – | 1432 | 2.58 | 169.8 | 101 | 180.2 | 1460 | – | – | –° | – |
| gatto | 0 | ok | 193 | 3725 | 1434 | 2.29 | 156.1 | 95 | 166.1 | 1430 | 0.16 | 0.20 | 315° | 0.00612 |
| gatto | 1 | timeout | – | – | 1455 | 1.95 | 170.5 | 99 | 180.0 | 1462 | – | – | –° | – |
| gatto | 2 | timeout | – | – | 1506 | 1.98 | 170.4 | 95 | 180.0 | 1456 | – | – | –° | – |
| pupazzo | 0 | ok | 354 | 2042 | 763 | 1.93 | 59.0 | 79 | 65.0 | 1115 | 0.01 | 0.03 | 45° | 0.04974 |
| pupazzo | 1 | ok | 377 | 3051 | 1140 | 1.64 | 101.8 | 87 | 109.6 | 1334 | 0.05 | 0.08 | 150° | 0.02229 |
| pupazzo | 2 | timeout | – | – | 1457 | 1.78 | 170.8 | 99 | 180.1 | 1465 | – | – | –° | – |
| sfera | 0 | ok | 28 | 132 | 49 | 1.88 | 3.0 | 62 | 5.2 | 756 | 0.09 | 0.12 | 15° | 0.01470 |
| sfera | 1 | ok | 35 | 210 | 79 | 1.72 | 4.8 | 63 | 6.9 | 771 | 0.12 | 0.14 | 225° | 0.00966 |
| sfera | 2 | ok | 19 | 98 | 37 | 1.72 | 2.3 | 62 | 4.3 | 750 | 0.10 | 0.13 | 195° | 0.01070 |
| bacchetta | 0 | ok | 265 | 1709 | 634 | 1.61 | 47.3 | 76 | 52.3 | 1052 | 0.13 | 0.33 | 105° | 0.00478 |
| bacchetta | 1 | ok | 328 | 1904 | 703 | 1.68 | 54.1 | 78 | 59.4 | 1089 | 0.26 | 0.31 | 120° | 0.00998 |
| bacchetta | 2 | ok | 274 | 1863 | 698 | 1.68 | 53.0 | 77 | 58.4 | 1082 | 0.22 | 0.29 | 75° | 0.00770 |
