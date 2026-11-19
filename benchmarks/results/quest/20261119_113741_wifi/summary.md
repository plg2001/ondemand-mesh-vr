# Sessione Quest: 20261119_113741_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.5:8000)
- Durata: 0.5 min (dalle 11:38:26, orologio del PC)
- Orologio del visore - orologio del PC: +7.03 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 1; mesh dalla cache: 0

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 150 | – |
| Caricamento del .glb, ms | 27 | – |
| Server, ms | 6102 | – |
| Totale, ms | 6281 | – |

Provenienza delle richieste secondo il server: 1 da 192.168.1.10 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| box_01 | meshpad | 28 | 6.1 | 0.66 | 0.62 | 0.71 | 0.00386 |

Il calcolo dell'accuratezza è compreso nel tempo del server: 131 ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): box_01 22.4 s
- Dalla cache: – ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 28 s, di cui 0 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- fps a regime: mediana 72 (55-73); secondi sotto il 90% dell'obiettivo: 1 (4%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 64 (55-72); dopo le altre dall'Edge: –; dopo quelle dalla cache: –; lontano dalle mesh: mediana 72 (70-73)
- Frame scartati (Stale) a regime: 22
- Tempo dell'app per frame (App): mediana 5.8 (4.4-7.2) ms
- Uso CPU / GPU: mediana 0.17 (0.13-0.21) / mediana 0.51 (0.42-0.67)
- Temperatura massima: 24 °C
