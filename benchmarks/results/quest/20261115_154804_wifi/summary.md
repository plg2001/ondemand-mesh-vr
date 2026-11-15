# Sessione Quest: 20261115_154804_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.5:8000)
- Durata: 0.6 min (dalle 15:48:11, orologio del PC)
- Orologio del visore - orologio del PC: +5.19 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 3

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 29 | mediana 29 (25-99) |
| Caricamento del .glb, ms | 28 | mediana 28 (28-28) |
| Server, ms | 2420 | mediana 2135 (2060-2189) |
| Totale, ms | 2479 | mediana 2201 (2118-2245) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| cat_01 | convex-hull | 266 | 2.4 | 0.16 | 0.15 | 0.18 | 0.00716 |
| wand_01 | convex-hull | 484 | 2.1 | 0.31 | 0.29 | 0.34 | 0.00231 |
| puppet_01 | convex-hull | 458 | 2.2 | 0.26 | 0.28 | 0.24 | 0.00408 |
| box_01 | convex-hull | 180 | 2.1 | 0.98 | 0.98 | 0.98 | 0.00005 |
| sphere_01 | convex-hull | 4090 | 2.1 | 1.00 | 1.00 | 1.00 | 0.00003 |
| bunny_01 | convex-hull | 572 | 2.2 | 0.39 | 0.37 | 0.41 | 0.00337 |

Il calcolo dell'accuratezza è compreso nel tempo del server: mediana 140 (52-368) ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): cat_01 2.5 s, wand_01 4.7 s, puppet_01 7.2 s, box_01 9.3 s, sphere_01 10.7 s, bunny_01 13.0 s
- Dalla cache: mediana 15.3 (15.1-15.4) ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 32 s, di cui 1 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1; frame scartati 20
- fps a regime: mediana 72 (51-74); secondi sotto il 90% dell'obiettivo: 2 (6%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 62 (51-72); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: mediana 72 (72-73); lontano dalle mesh: mediana 72 (61-74)
- Frame scartati (Stale) a regime: 20
- Tempo dell'app per frame (App): mediana 6.5 (1.3-14.0) ms
- Uso CPU / GPU: mediana 0.20 (0.14-0.27) / mediana 0.56 (0.33-0.64)
- Temperatura massima: 26 °C
