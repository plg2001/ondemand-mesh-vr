# MeshPad su schizzi (2026-11-18 17:35)

- Dispositivo: decoder **cuda** (NVIDIA GeForce GT 1030), encoder RADIO **cpu**, precisione fp16-full, attenzione sdpa-shim
- Caricamento del modello: 8.4 s (picco RAM 2.86 GB)
- Vista degli schizzi: azimut 45°, elevazione 30°; generazione campionata come nella demo (top-k 10, top-p 0,8, temperatura 0,5); limite 90 s
- Accuratezza rispetto alla nuvola densa della mesh originale (F-score all'1% della diagonale, Chamfer-L2), sia com'è uscita sia ruotata nel modo migliore attorno all'asse verticale (MeshPad sceglie da solo il davanti di un oggetto).

| Forma | Seme | Esito | Triangoli | Token | Passi decoder | Encoder [s] | Generazione [s] | ms/passo (p50) | Totale [s] | VRAM picco [MiB] | F-score com'è | F-score ruotata | rotazione | Chamfer-L2 ruotata |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| sedia tonda | 1 | ok | 213 | 1071 | 398 | 2.33 | 27.8 | 70 | 33.6 | 934 | 0.61 | 0.61 | 0° | 0.00056 |
| sedia tonda | 2 | ok | 250 | 1268 | 467 | 1.93 | 33.1 | 73 | 37.7 | 969 | 0.62 | 0.62 | 0° | 0.00044 |
| tavolo | 1 | ok | 110 | 524 | 193 | 2.40 | 12.4 | 67 | 15.8 | 832 | 0.69 | 0.69 | 0° | 0.00534 |
| tavolo | 2 | ok | 100 | 698 | 271 | 1.89 | 17.1 | 67 | 20.4 | 868 | 0.51 | 0.51 | 0° | 0.00546 |
| libreria | 1 | ok | 152 | 854 | 323 | 2.02 | 21.1 | 69 | 24.8 | 892 | 0.40 | 0.41 | 180° | 0.00206 |
| libreria | 2 | ok | 212 | 1252 | 463 | 1.75 | 32.6 | 72 | 36.9 | 969 | 0.66 | 0.66 | 0° | 0.00188 |
| lampada | 1 | ok | 309 | 1450 | 531 | 1.73 | 38.7 | 75 | 43.3 | 1014 | 0.71 | 0.71 | 90° | 0.00057 |
| lampada | 2 | ok | 112 | 643 | 238 | 1.80 | 15.5 | 68 | 18.6 | 854 | 0.63 | 0.63 | 270° | 0.00098 |
| panca | 1 | ok | 225 | 1094 | 405 | 1.74 | 27.8 | 71 | 31.7 | 936 | 0.67 | 0.67 | 0° | 0.00043 |
| panca | 2 | ok | 205 | 1009 | 374 | 1.74 | 25.5 | 71 | 29.3 | 921 | 0.68 | 0.68 | 0° | 0.00090 |
