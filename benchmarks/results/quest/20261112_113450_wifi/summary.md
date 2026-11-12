# Sessione Quest: 20261112_113450_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.27:8000)
- Durata: 1.4 min (dalle 11:35:10, orologio del PC)
- Orologio del visore - orologio del PC: +1.24 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 1; mesh dalla cache: 0

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 147 | – |
| Caricamento del .glb, ms | 28 | – |
| Server, ms | 65964 | – |
| Totale, ms | 66141 | – |

Provenienza delle richieste secondo il server: 1 da 192.168.1.28 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| box_01 | meshanything | 340 | 66.0 | 0.70 | 0.60 | 0.85 | 0.00101 |

Il calcolo dell'accuratezza è compreso nel tempo del server: 91 ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): box_01 66.1 s
- Dalla cache: – ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 81 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 1; frame scartati 149
- fps a regime: mediana 72 (37-74); secondi sotto il 90% dell'obiettivo: 2 (3%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 54 (37-72); dopo le altre dall'Edge: –; dopo quelle dalla cache: –; lontano dalle mesh: mediana 72 (61-74)
- Frame scartati (Stale) a regime: 55
- Tempo dell'app per frame (App): mediana 6.5 (1.1-6.9) ms
- Uso CPU / GPU: mediana 0.20 (0.11-0.30) / mediana 0.56 (0.21-0.59)
- Temperatura massima: 30 °C
