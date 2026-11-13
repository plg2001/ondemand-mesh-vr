# Sessione Quest: 20261113_113343_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.32:8000)
- Durata: 0.4 min (dalle 11:33:48, orologio del PC)
- Orologio del visore - orologio del PC: +1.95 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 3

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 84 | mediana 54 (52-117) |
| Caricamento del .glb, ms | 28 | mediana 28 (27-30) |
| Server, ms | 327 | mediana 360 (298-453) |
| Totale, ms | 440 | mediana 489 (400-534) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| bunny_01 | poisson | 13215 | 0.3 | 0.93 | 0.93 | 0.93 | 0.00008 |
| sphere_01 | poisson | 10557 | 0.3 | 0.98 | 1.00 | 0.96 | 0.00005 |
| box_01 | poisson | 8525 | 0.5 | 0.90 | 0.94 | 0.86 | 0.00011 |
| wand_01 | poisson | 14283 | 0.4 | 0.79 | 0.86 | 0.73 | 0.00019 |
| puppet_01 | poisson | 18617 | 0.4 | 0.56 | 0.46 | 0.71 | 0.00755 |
| cat_01 | poisson | 17407 | 0.4 | 0.74 | 0.66 | 0.84 | 0.00070 |

Il calcolo dell'accuratezza è compreso nel tempo del server: mediana 66 (54-119) ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): bunny_01 0.4 s, sphere_01 0.4 s, box_01 1.7 s, wand_01 1.3 s, puppet_01 0.6 s, cat_01 1.2 s
- Dalla cache: mediana 15.7 (15.5-15.8) ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 23 s, di cui 1 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 73; frame scartati 62
- fps a regime: mediana 72 (51-72); secondi sotto il 90% dell'obiettivo: 2 (9%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 61 (51-71); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: mediana 72 (72-72); lontano dalle mesh: mediana 72 (61-72)
- Frame scartati (Stale) a regime: 39
- Tempo dell'app per frame (App): mediana 7.5 (1.6-8.7) ms
- Uso CPU / GPU: mediana 0.17 (0.12-0.26) / mediana 0.70 (0.35-0.77)
- Temperatura massima: 24 °C
