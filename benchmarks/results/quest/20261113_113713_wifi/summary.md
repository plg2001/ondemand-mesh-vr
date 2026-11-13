# Sessione Quest: 20261113_113713_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.32:8000)
- Durata: 0.4 min (dalle 11:37:19, orologio del PC)
- Orologio del visore - orologio del PC: +1.96 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 1

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 110 | mediana 30 (30-94) |
| Caricamento del .glb, ms | 29 | mediana 28 (27-32) |
| Server, ms | 104 | mediana 114 (96-227) |
| Totale, ms | 244 | mediana 178 (154-290) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| cat_01 | alpha | 4224 | 0.1 | 0.86 | 0.83 | 0.89 | 0.00012 |
| puppet_01 | alpha | 2856 | 0.1 | 0.71 | 0.78 | 0.66 | 0.00027 |
| box_01 | alpha | 1960 | 0.2 | 0.52 | 0.68 | 0.42 | 0.00351 |
| wand_01 | alpha | 2858 | 0.1 | 0.83 | 0.98 | 0.72 | 0.00009 |
| sphere_01 | alpha | 280 | 0.1 | 0.16 | 1.00 | 0.09 | 0.00533 |
| bunny_01 | alpha | 4144 | 0.1 | 0.87 | 0.91 | 0.82 | 0.00013 |

Il calcolo dell'accuratezza è compreso nel tempo del server: mediana 64 (56-101) ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): cat_01 0.2 s, puppet_01 0.7 s, box_01 0.3 s, wand_01 0.2 s, sphere_01 0.3 s, bunny_01 0.5 s
- Dalla cache: 15.2 ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 21 s, di cui 1 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 73; frame scartati 58
- fps a regime: mediana 72 (50-72); secondi sotto il 90% dell'obiettivo: 2 (10%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 60 (50-71); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: mediana 72 (72-72); lontano dalle mesh: mediana 72 (62-72)
- Frame scartati (Stale) a regime: 39
- Tempo dell'app per frame (App): mediana 6.8 (1.4-7.8) ms
- Uso CPU / GPU: mediana 0.16 (0.11-0.25) / mediana 0.59 (0.23-0.63)
- Temperatura massima: 26 °C
