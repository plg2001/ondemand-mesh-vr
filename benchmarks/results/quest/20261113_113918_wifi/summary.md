# Sessione Quest: 20261113_113918_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.32:8000)
- Durata: 0.3 min (dalle 11:39:23, orologio del PC)
- Orologio del visore - orologio del PC: +1.96 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 0

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 53 | mediana 96 (55-162) |
| Caricamento del .glb, ms | 30 | mediana 28 (27-29) |
| Server, ms | 165 | mediana 138 (56-354) |
| Totale, ms | 250 | mediana 280 (140-546) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| bunny_01 | hull | 572 | 0.2 | 0.39 | 0.37 | 0.41 | 0.00337 |
| box_01 | hull | 180 | 0.1 | 0.98 | 0.98 | 0.98 | 0.00005 |
| sphere_01 | hull | 4090 | 0.1 | 1.00 | 1.00 | 1.00 | 0.00003 |
| wand_01 | hull | 484 | 0.1 | 0.31 | 0.29 | 0.34 | 0.00231 |
| puppet_01 | hull | 458 | 0.2 | 0.26 | 0.28 | 0.24 | 0.00408 |
| cat_01 | hull | 266 | 0.4 | 0.16 | 0.15 | 0.18 | 0.00716 |

Il calcolo dell'accuratezza è compreso nel tempo del server: mediana 142 (48-345) ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): bunny_01 0.3 s, box_01 0.1 s, sphere_01 0.3 s, wand_01 0.6 s, puppet_01 0.3 s, cat_01 0.9 s
- Dalla cache: – ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 17 s, di cui 1 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 75; frame scartati 1
- fps a regime: mediana 72 (42-72); secondi sotto il 90% dell'obiettivo: 2 (12%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 57 (42-72); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: –; lontano dalle mesh: mediana 72 (53-72)
- Frame scartati (Stale) a regime: 60
- Tempo dell'app per frame (App): mediana 6.8 (1.1-9.2) ms
- Uso CPU / GPU: mediana 0.22 (0.11-0.34) / mediana 0.66 (0.42-0.74)
- Temperatura massima: 26 °C
