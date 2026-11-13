# Metodi classici contro modelli generativi

Stessi ingressi (`data/inputs/<forma>_n<punti>.ply`), stesso riferimento (nuvola densa della mesh originale) e stessa misura (`quality_metrics`) dello sweep dei modelli generativi. Accuratezza: F-score all'1% della diagonale (più alto è meglio), precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2 (più basso è meglio). Normali *vere*: della mesh originale; *stimate*: dai punti, come fa il server per il visore. Tempi dei metodi classici: mediana di 3 prove sulla CPU di questo PC, stima delle normali esclusa; dei generativi: generazione.

## F-score per forma (mesh a piena risoluzione)

| Forma | Involucro | Alpha | BPA (stimate) | Poisson (stimate) | BPA (vere) | Poisson (vere) | Miglior generativo |
|---|---|---|---|---|---|---|---|
| scatola | 0.99 | 0.52 | 0.97 | 0.91 | 0.97 | 0.94 | 0.23 (meshanything, 73.7 s) |
| coniglio | 0.39 | 0.88 | 0.95 | 0.88 | 0.97 | 0.97 | nessuna mesh completa |
| pupazzo | 0.27 | 0.73 | 0.87 | 0.72 | 0.88 | 0.89 | 0.47 (fastmesh, 225.2 s) |
| sfera | 1.00 | 0.20 | 1.00 | 0.98 | 1.00 | 0.98 | nessuna mesh completa |
| gatto | 0.17 | 0.86 | 0.92 | 0.71 | 0.95 | 0.97 | nessuna mesh completa |
| bacchetta | 0.32 | 0.82 | 0.88 | 0.82 | 0.97 | 0.97 | nessuna mesh completa |

## Dettaglio

### scatola (`box`)

| Metodo | Normali | Triangoli | Tempo | F-score | Precisione | Richiamo | Chamfer-L2 | Chiusa |
|---|---|---|---|---|---|---|---|---|
| convex hull | non usate | 174 | 10 ms | 0.987 | 1.00 | 0.98 | 0.00004 | sì |
| convex hull ridotta a 1000 | non usate | 174 | 10 ms | 0.987 | 1.00 | 0.98 | 0.00004 | sì |
| alpha shape | non usate | 1918 | 247 ms | 0.523 | 0.72 | 0.41 | 0.00346 | no |
| alpha shape ridotta a 1000 | non usate | 1000 | 237 ms | 0.522 | 0.72 | 0.41 | 0.00346 | no |
| Ball Pivoting | vere | 4017 | 53 ms | 0.969 | 1.00 | 0.94 | 0.00005 | no |
| Ball Pivoting ridotta a 1000 | vere | 999 | 94 ms | 0.969 | 0.99 | 0.95 | 0.00005 | no |
| Ball Pivoting | stimate | 4012 | 52 ms | 0.968 | 0.99 | 0.94 | 0.00005 | no |
| Ball Pivoting ridotta a 1000 | stimate | 1000 | 97 ms | 0.967 | 1.00 | 0.94 | 0.00005 | no |
| Poisson | vere | 8262 | 487 ms | 0.935 | 0.99 | 0.89 | 0.00008 | no |
| Poisson ridotta a 1000 | vere | 999 | 665 ms | 0.938 | 0.99 | 0.89 | 0.00008 | no |
| Poisson | stimate | 8512 | 524 ms | 0.915 | 0.98 | 0.86 | 0.00009 | no |
| Poisson ridotta a 1000 | stimate | 1000 | 601 ms | 0.914 | 0.97 | 0.86 | 0.00009 | no |
| meshanything default (cpu, fp32) | vere | 336 | 274.3 s | 0.230 | 0.17 | 0.37 | 0.00468 |  |
| meshanything default (cuda, fp16-full) | vere | 336 | 73.7 s | 0.230 | 0.17 | 0.37 | 0.00468 |  |

### coniglio (`bunny`)

| Metodo | Normali | Triangoli | Tempo | F-score | Precisione | Richiamo | Chamfer-L2 | Chiusa |
|---|---|---|---|---|---|---|---|---|
| convex hull | non usate | 608 | 10 ms | 0.390 | 0.38 | 0.40 | 0.00347 | sì |
| convex hull ridotta a 1000 | non usate | 608 | 9 ms | 0.390 | 0.38 | 0.40 | 0.00347 | sì |
| alpha shape | non usate | 4240 | 58 ms | 0.881 | 0.93 | 0.84 | 0.00011 | no |
| alpha shape ridotta a 1000 | non usate | 1908 | 91 ms | 0.647 | 0.83 | 0.53 | 0.00106 | no |
| Ball Pivoting | vere | 3736 | 72 ms | 0.968 | 0.99 | 0.95 | 0.00004 | no |
| Ball Pivoting ridotta a 1000 | vere | 1000 | 105 ms | 0.957 | 0.98 | 0.93 | 0.00005 | no |
| Ball Pivoting | stimate | 3579 | 42 ms | 0.949 | 0.98 | 0.92 | 0.00005 | no |
| Ball Pivoting ridotta a 1000 | stimate | 1000 | 67 ms | 0.932 | 0.98 | 0.88 | 0.00007 | no |
| Poisson | vere | 12663 | 318 ms | 0.968 | 1.00 | 0.94 | 0.00005 | no |
| Poisson ridotta a 1000 | vere | 999 | 424 ms | 0.965 | 0.99 | 0.94 | 0.00006 | no |
| Poisson | stimate | 13440 | 333 ms | 0.882 | 0.87 | 0.90 | 0.00019 | no |
| Poisson ridotta a 1000 | stimate | 999 | 468 ms | 0.874 | 0.87 | 0.88 | 0.00018 | no |

### pupazzo (`fastmesh_001`)

| Metodo | Normali | Triangoli | Tempo | F-score | Precisione | Richiamo | Chamfer-L2 | Chiusa |
|---|---|---|---|---|---|---|---|---|
| convex hull | non usate | 478 | 7 ms | 0.267 | 0.30 | 0.24 | 0.00409 | sì |
| convex hull ridotta a 1000 | non usate | 478 | 7 ms | 0.267 | 0.30 | 0.24 | 0.00409 | sì |
| alpha shape | non usate | 3030 | 64 ms | 0.735 | 0.81 | 0.67 | 0.00023 | no |
| alpha shape ridotta a 1000 | non usate | 1000 | 89 ms | 0.594 | 0.73 | 0.50 | 0.00131 | no |
| Ball Pivoting | vere | 3160 | 49 ms | 0.877 | 0.97 | 0.80 | 0.00012 | no |
| Ball Pivoting ridotta a 1000 | vere | 1000 | 72 ms | 0.852 | 0.97 | 0.76 | 0.00030 | no |
| Ball Pivoting | stimate | 3140 | 53 ms | 0.873 | 0.97 | 0.79 | 0.00010 | no |
| Ball Pivoting ridotta a 1000 | stimate | 999 | 179 ms | 0.843 | 0.97 | 0.74 | 0.00015 | no |
| Poisson | vere | 12877 | 567 ms | 0.888 | 0.96 | 0.82 | 0.00011 | no |
| Poisson ridotta a 1000 | vere | 1000 | 1.5 s | 0.873 | 0.96 | 0.80 | 0.00014 | no |
| Poisson | stimate | 15985 | 896 ms | 0.721 | 0.67 | 0.79 | 0.00262 | no |
| Poisson ridotta a 1000 | stimate | 999 | 2.6 s | 0.714 | 0.67 | 0.76 | 0.00190 | no |
| fastmesh V1K (cpu, fp32) | vere | 5192 | 225.2 s | 0.473 | 0.56 | 0.41 | 0.00041 |  |

### sfera (`sphere`)

| Metodo | Normali | Triangoli | Tempo | F-score | Precisione | Richiamo | Chamfer-L2 | Chiusa |
|---|---|---|---|---|---|---|---|---|
| convex hull | non usate | 4092 | 60 ms | 0.999 | 1.00 | 1.00 | 0.00002 | sì |
| convex hull ridotta a 1000 | non usate | 1000 | 92 ms | 0.999 | 1.00 | 1.00 | 0.00003 | sì |
| alpha shape | non usate | 342 | 57 ms | 0.198 | 1.00 | 0.11 | 0.00305 | no |
| alpha shape ridotta a 1000 | non usate | 342 | 94 ms | 0.198 | 1.00 | 0.11 | 0.00305 | no |
| Ball Pivoting | vere | 4081 | 90 ms | 0.997 | 1.00 | 0.99 | 0.00003 | no |
| Ball Pivoting ridotta a 1000 | vere | 999 | 139 ms | 0.998 | 1.00 | 1.00 | 0.00003 | no |
| Ball Pivoting | stimate | 4081 | 70 ms | 0.997 | 1.00 | 0.99 | 0.00003 | no |
| Ball Pivoting ridotta a 1000 | stimate | 999 | 138 ms | 0.998 | 1.00 | 1.00 | 0.00003 | no |
| Poisson | vere | 10417 | 718 ms | 0.983 | 1.00 | 0.97 | 0.00003 | no |
| Poisson ridotta a 1000 | vere | 1000 | 1.1 s | 0.983 | 1.00 | 0.97 | 0.00004 | no |
| Poisson | stimate | 10411 | 1.6 s | 0.982 | 1.00 | 0.96 | 0.00004 | no |
| Poisson ridotta a 1000 | stimate | 1000 | 438 ms | 0.983 | 1.00 | 0.97 | 0.00004 | no |

### gatto (`tmg_cat`)

| Metodo | Normali | Triangoli | Tempo | F-score | Precisione | Richiamo | Chamfer-L2 | Chiusa |
|---|---|---|---|---|---|---|---|---|
| convex hull | non usate | 280 | 6 ms | 0.165 | 0.16 | 0.17 | 0.00714 | sì |
| convex hull ridotta a 1000 | non usate | 280 | 6 ms | 0.165 | 0.16 | 0.17 | 0.00714 | sì |
| alpha shape | non usate | 4064 | 57 ms | 0.859 | 0.84 | 0.88 | 0.00013 | no |
| alpha shape ridotta a 1000 | non usate | 1286 | 73 ms | 0.623 | 0.65 | 0.60 | 0.00052 | no |
| Ball Pivoting | vere | 3511 | 52 ms | 0.949 | 0.98 | 0.92 | 0.00005 | no |
| Ball Pivoting ridotta a 1000 | vere | 999 | 62 ms | 0.929 | 0.98 | 0.88 | 0.00007 | no |
| Ball Pivoting | stimate | 3234 | 44 ms | 0.919 | 0.98 | 0.87 | 0.00006 | no |
| Ball Pivoting ridotta a 1000 | stimate | 1000 | 62 ms | 0.883 | 0.98 | 0.80 | 0.00010 | no |
| Poisson | vere | 14206 | 272 ms | 0.971 | 1.00 | 0.94 | 0.00004 | no |
| Poisson ridotta a 1000 | vere | 1000 | 844 ms | 0.956 | 1.00 | 0.92 | 0.00006 | no |
| Poisson | stimate | 16562 | 762 ms | 0.708 | 0.63 | 0.80 | 0.00146 | no |
| Poisson ridotta a 1000 | stimate | 999 | 816 ms | 0.688 | 0.62 | 0.77 | 0.00143 | no |

### bacchetta (`mav2_wand`)

| Metodo | Normali | Triangoli | Tempo | F-score | Precisione | Richiamo | Chamfer-L2 | Chiusa |
|---|---|---|---|---|---|---|---|---|
| convex hull | non usate | 430 | 9 ms | 0.319 | 0.31 | 0.33 | 0.00230 | sì |
| convex hull ridotta a 1000 | non usate | 430 | 8 ms | 0.319 | 0.31 | 0.33 | 0.00230 | sì |
| alpha shape | non usate | 2746 | 65 ms | 0.816 | 0.97 | 0.71 | 0.00009 | no |
| alpha shape ridotta a 1000 | non usate | 1000 | 79 ms | 0.815 | 0.96 | 0.71 | 0.00009 | no |
| Ball Pivoting | vere | 3471 | 64 ms | 0.965 | 1.00 | 0.94 | 0.00003 | no |
| Ball Pivoting ridotta a 1000 | vere | 999 | 74 ms | 0.936 | 1.00 | 0.88 | 0.00004 | no |
| Ball Pivoting | stimate | 2784 | 43 ms | 0.880 | 1.00 | 0.79 | 0.00007 | no |
| Ball Pivoting ridotta a 1000 | stimate | 999 | 54 ms | 0.834 | 1.00 | 0.72 | 0.00010 | no |
| Poisson | vere | 17810 | 548 ms | 0.974 | 1.00 | 0.95 | 0.00004 | no |
| Poisson ridotta a 1000 | vere | 1000 | 598 ms | 0.963 | 1.00 | 0.93 | 0.00006 | no |
| Poisson | stimate | 13902 | 591 ms | 0.815 | 0.92 | 0.73 | 0.00013 | no |
| Poisson ridotta a 1000 | stimate | 999 | 622 ms | 0.806 | 0.93 | 0.71 | 0.00013 | no |

Non in tabella: 8 run dei modelli generativi su queste forme senza una mesh completa (troncati di proposito per misurare i ms/token, oppure fermati dal limite di tempo).
