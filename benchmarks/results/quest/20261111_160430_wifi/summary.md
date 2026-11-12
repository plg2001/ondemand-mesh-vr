# Sessione Quest: 20261111_160430_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.27:8000)
- Durata: 1.5 min (dalle 16:04:56, orologio del PC)
- Orologio del visore - orologio del PC: +0.39 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 1; mesh dalla cache: 0

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 22 | – |
| Caricamento del .glb, ms | 60 | – |
| Server, ms | 66351 | – |
| Totale, ms | 66434 | – |

Provenienza delle richieste secondo il server: 1 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| box_01 | meshanything | 340 | 66.4 | – | – | – | – |

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): box_01 34.0 s
- Dalla cache: – ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 89 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 74; frame scartati 56
- fps a regime: mediana 72 (61-73); secondi sotto il 90% dell'obiettivo: 3 (3%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 61 (61-61); dopo le altre dall'Edge: –; dopo quelle dalla cache: –; lontano dalle mesh: mediana 72 (64-73)
- Frame scartati (Stale) a regime: 55
- Tempo dell'app per frame (App): mediana 6.9 (1.1-9.7) ms
- Uso CPU / GPU: mediana 0.19 (0.10-0.28) / mediana 0.59 (0.29-0.66)
- Temperatura massima: 26 °C
