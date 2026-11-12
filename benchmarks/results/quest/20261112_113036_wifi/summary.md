# Sessione Quest: 20261112_113036_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.27:8000)
- Durata: 0.8 min (dalle 11:30:46, orologio del PC)
- Orologio del visore - orologio del PC: +1.23 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 1

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 45 | mediana 36 (30-112) |
| Caricamento del .glb, ms | 28 | mediana 28 (28-28) |
| Server, ms | 2496 | mediana 2135 (2056-2181) |
| Totale, ms | 2571 | mediana 2216 (2118-2298) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| cat_01 | convex-hull | 266 | 2.5 | 0.16 | 0.15 | 0.18 | 0.00716 |
| puppet_01 | convex-hull | 458 | 2.2 | 0.26 | 0.28 | 0.24 | 0.00408 |
| wand_01 | convex-hull | 484 | 2.1 | 0.31 | 0.29 | 0.34 | 0.00231 |
| sphere_01 | convex-hull | 4090 | 2.1 | 1.00 | 1.00 | 1.00 | 0.00003 |
| box_01 | convex-hull | 180 | 2.1 | 0.98 | 0.98 | 0.98 | 0.00005 |
| bunny_01 | convex-hull | 572 | 2.2 | 0.39 | 0.37 | 0.41 | 0.00337 |

Il calcolo dell'accuratezza è compreso nel tempo del server: mediana 139 (50-354) ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): cat_01 2.6 s, puppet_01 3.5 s, wand_01 5.3 s, sphere_01 6.8 s, box_01 9.7 s, bunny_01 11.2 s
- Dalla cache: 15.6 ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 28 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 74; frame scartati 57
- fps a regime: mediana 72 (54-72); secondi sotto il 90% dell'obiettivo: 2 (8%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 62 (54-70); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: 72; lontano dalle mesh: mediana 72 (62-72)
- Frame scartati (Stale) a regime: 54
- Tempo dell'app per frame (App): mediana 6.4 (1.6-7.2) ms
- Uso CPU / GPU: mediana 0.16 (0.11-0.24) / mediana 0.54 (0.28-0.59)
- Temperatura massima: 28 °C
