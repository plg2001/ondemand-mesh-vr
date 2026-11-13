# Sessione Quest: 20261113_112816_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.32:8000)
- Durata: 0.5 min (dalle 11:28:22, orologio del PC)
- Orologio del visore - orologio del PC: +1.94 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 2

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 64 | mediana 29 (21-55) |
| Caricamento del .glb, ms | 28 | mediana 28 (28-29) |
| Server, ms | 147 | mediana 152 (140-242) |
| Totale, ms | 241 | mediana 210 (201-308) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| bunny_01 | ball_pivoting | 3607 | 0.1 | 0.95 | 0.98 | 0.93 | 0.00006 |
| box_01 | ball_pivoting | 4006 | 0.2 | 0.96 | 0.97 | 0.95 | 0.00006 |
| sphere_01 | ball_pivoting | 4083 | 0.1 | 1.00 | 1.00 | 1.00 | 0.00003 |
| wand_01 | ball_pivoting | 2780 | 0.2 | 0.88 | 1.00 | 0.78 | 0.00008 |
| puppet_01 | ball_pivoting | 3105 | 0.2 | 0.88 | 0.98 | 0.80 | 0.00010 |
| cat_01 | ball_pivoting | 3151 | 0.1 | 0.90 | 0.98 | 0.84 | 0.00009 |

Il calcolo dell'accuratezza è compreso nel tempo del server: mediana 55 (53-62) ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): bunny_01 0.3 s, box_01 0.9 s, sphere_01 0.2 s, wand_01 0.5 s, puppet_01 0.2 s, cat_01 0.2 s
- Dalla cache: mediana 30.5 (29.1-31.9) ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 24 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 74; frame scartati 62
- fps a regime: mediana 72 (52-73); secondi sotto il 90% dell'obiettivo: 2 (9%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 52 (52-53); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: mediana 72 (72-73); lontano dalle mesh: mediana 72 (72-73)
- Frame scartati (Stale) a regime: 47
- Tempo dell'app per frame (App): mediana 7.0 (1.5-13.6) ms
- Uso CPU / GPU: mediana 0.16 (0.13-0.27) / mediana 0.63 (0.37-0.74)
- Temperatura massima: 23 °C
