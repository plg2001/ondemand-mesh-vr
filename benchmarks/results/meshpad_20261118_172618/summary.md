# MeshPad su schizzi (2026-11-18 17:26)

- Dispositivo: decoder **cuda** (NVIDIA GeForce GT 1030), encoder RADIO **cpu**, precisione fp16-full, attenzione sdpa-shim
- Caricamento del modello: 7.9 s (picco RAM 3.21 GB)
- Vista degli schizzi: azimut 45°, elevazione 30°; generazione campionata come nella demo (top-k 10, top-p 0,8, temperatura 0,5); limite 90 s
- Accuratezza rispetto alla nuvola densa della mesh originale (F-score all'1% della diagonale, Chamfer-L2), sia com'è uscita sia ruotata nel modo migliore attorno all'asse verticale (MeshPad sceglie da solo il davanti di un oggetto).

| Forma | Seme | Esito | Triangoli | Token | Passi decoder | Encoder [s] | Generazione [s] | ms/passo (p50) | Totale [s] | VRAM picco [MiB] | F-score com'è | F-score ruotata | rotazione | Chamfer-L2 ruotata |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| sedia | 0 | ok | 80 | 477 | 182 | 1.97 | 11.5 | 65 | 15.6 | 823 | 0.39 | 0.39 | 0° | 0.00085 |
| sedia tonda | 0 | ok | 197 | 884 | 323 | 1.80 | 21.4 | 68 | 25.0 | 907 | 0.67 | 0.67 | 0° | 0.00097 |
| divano | 0 | ok | 303 | 1607 | 598 | 1.82 | 43.7 | 75 | 48.8 | 1033 | 0.51 | 0.51 | 0° | 0.00045 |
| panca | 0 | ok | 97 | 657 | 252 | 1.81 | 16.2 | 67 | 19.5 | 855 | 0.64 | 0.64 | 0° | 0.00302 |
| letto | 0 | ok | 66 | 628 | 237 | 2.02 | 14.9 | 67 | 18.2 | 854 | 0.06 | 0.16 | 270° | 0.00460 |
| libreria | 0 | ok | 180 | 1015 | 382 | 1.92 | 25.5 | 70 | 29.5 | 924 | 0.41 | 0.41 | 180° | 0.00235 |
| tavolo | 0 | ok | 116 | 655 | 248 | 2.35 | 15.7 | 67 | 19.4 | 854 | 0.50 | 0.50 | 0° | 0.00323 |
| tavolo tondo | 0 | timeout | – | – | 987 | 2.30 | 82.6 | 84 | 90.0 | 1220 | – | – | –° | – |
| scrivania | 0 | ok | 121 | 693 | 258 | 2.38 | 17.0 | 69 | 20.9 | 868 | 0.22 | 0.23 | 180° | 0.00408 |
| lampada | 0 | ok | 46 | 263 | 100 | 2.38 | 6.4 | 66 | 9.4 | 781 | 0.46 | 0.46 | 180° | 0.00132 |
| monitor | 0 | ok | 336 | 1513 | 548 | 2.41 | 41.2 | 77 | 46.8 | 1015 | 0.41 | 0.41 | 0° | 0.00066 |
| portatile | 0 | ok | 107 | 600 | 225 | 2.14 | 14.7 | 68 | 18.1 | 845 | 0.33 | 0.28 | 345° | 0.00390 |
| cestino | 0 | ok | 99 | 673 | 256 | 2.44 | 16.5 | 68 | 20.9 | 858 | 0.28 | 0.40 | 120° | 0.00236 |
| microonde | 0 | timeout | – | – | 965 | 2.56 | 82.4 | 85 | 90.0 | 1217 | – | – | –° | – |
