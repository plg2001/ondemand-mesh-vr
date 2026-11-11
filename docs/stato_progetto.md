# Stato del progetto (11 novembre 2026)

Riassunto per riprendere il lavoro, per te e per chi apre una nuova conversazione con l'assistente. Descrive **dove
siamo, cosa è stato misurato, cosa non è stato verificato e cosa resta da fare**. Come è fatto il codice è in
`CLAUDE.md` (architettura) e `README.md` (uso); la procedura per il Quest è in [guida_quest.md](guida_quest.md).

## 1. Obiettivo (dalle risposte della relatrice, Claudia)

Ricostruire oggetti 3D **on demand** per visori standalone (Meta Quest 2 / 3S):

- la scena 3D esiste già (mesh preparate a monte) e sul visore viene conservata come **nuvole di punti sparse**,
  una per oggetto (per ora: i punti stanno già sul visore; provare anche con i punti sull'Edge è "un esperimento
  forse utile", non richiesto);
- quando un oggetto **entra nel campo visivo**, il visore ne manda i punti a un nodo **Edge**, che ricostruisce la
  mesh con uno dei tre modelli generativi (MeshAnything V2, FastMesh, TreeMeshGPT) e la rimanda;
- i modelli ricostruiscono **l'oggetto intero**, non la parte visibile;
- gli oggetti fuori vista tornano a essere solo punti.

Termine usato da Claudia: **on demand** (non "streaming").

## 2. Fase 1: costo dei modelli (fatta)

Misure su questo PC (i5-9400, 8 GB RAM, GeForce GT 1030 da 2 GB), report in `benchmarks/results/final_*`, tabelle in
[risultati_benchmark.md](risultati_benchmark.md), analisi in [requisiti_calcolo.md](requisiti_calcolo.md).

- Il costo **non dipende dalla densità dei punti** (100, 500 o 2048: ±4%): dipende dal numero di facce generate.
- Generazioni **complete** disponibili: MeshAnything V2 sulla scatola (GPU fp16: 74 s; CPU: 274 s) e FastMesh sul
  pupazzetto (CPU: 225 s, 1112 vertici, 5192 triangoli). **TreeMeshGPT: solo run troncati a 64 token, mai una mesh
  completa.**
- Qualità (Chamfer-L2 ↓ / F-score@1% ↑): FastMesh pupazzetto 0,00041 / 0,47; MeshAnything scatola 0,0047 / 0,23.
  **Non confrontabili** tra loro: forme diverse. Solo nuvole pulite campionate da mesh esistenti.
- Stime (non misure) sul visore, fp16, mesh da 800 facce: Quest 2 da circa 27 s (FastMesh) a 219 s (MeshAnything
  V2); sull'Edge (stima per una RTX 4090) 2,6 / 8,8 / 18,4 s (FastMesh / TreeMeshGPT / MeshAnything V2).

## 3. Prototipo costruito (Fase 2)

| Parte | Dove | Cosa fa |
|---|---|---|
| Server Edge | `edge/`, `start_edge_server.bat` | FastAPI: riceve i punti (float32 grezzi), risponde con la mesh `.glb`; backend **mock** (involucro convesso, ritardo simulato) o **modello** vero; una richiesta alla volta |
| Scena di prova | `data/scenes/` | `build_scene.py` + `demo_room.yaml`: 6 oggetti, ognuno come nuvola da 2048 punti (144 KB in tutto, contro 2743 KB delle mesh originali) |
| App Unity | `unity/OnDemandMeshVR` | punti come segnaposto, richiesta all'Edge quando l'oggetto è inquadrato, rilascio dopo 3 s fuori vista, cache dei `.glb`, pannello di stato nel visore, log CSV dei tempi |
| Quest | `tools/quest/`, menu Unity **Tools → Quest** | build dell'APK, installazione, server via cavo o Wi-Fi, log dal visore, copia dei risultati |

Stato sul visore: l'app è stata **provata sul Quest 2**: l'analogico difettoso non muove più nulla
(`disableStickLocomotion`) e il comportamento è uguale a quello nel simulatore. **Dal visore non sono ancora stati
recuperati numeri** (`tools\quest\pull_results.bat`).

## 4. Misure fatte con il prototipo (sul PC, backend mock da 2 s)

- Rete locale sullo stesso PC: **circa 6-12 ms**. La **prima richiesta di ogni Play** costa da 1 a 2,7 s in più
  (avvio dell'editor, varia da prova a prova): va riportata a parte.
- La coda è seriale: la latenza percepita cresce di **circa 2 s per ogni oggetto davanti** (con 6 oggetti in vista
  l'ultimo compare dopo 7-9 s). Con un modello vero, più lento del mock, peggiora.
- Una mesh già in cache ricompare in **6-15 ms**, senza richiesta.
- Memoria: le nuvole da 2048 punti (24 KB l'una) risparmiano molto solo per oggetti con tante facce (coniglio 1222 KB,
  gatto 999 KB), poco o nulla per quelli semplici (scatola 0,2 KB). Una mesh generata da 800 triangoli pesa circa
  14 KB, **meno di una nuvola da 2048 punti**: il vantaggio è rispetto alle mesh originali dense, non a quelle
  generate. Da rimisurare con le mesh dei modelli veri e con meno punti (500 punti = 5,9 KB).

## 5. Cosa NON è stato verificato

- Il **backend con un modello vero** non è mai stato provato end to end con Unity (sul PC di sviluppo MeshAnything V2
  impiegherebbe circa 75 s per la scatola sulla GPU; con Unity aperto la memoria è al limite).
- La **rete Wi-Fi** del visore (`server_wifi.bat`, firewall di Windows): preparata e documentata, ma nessuna misura.
- Il **tracciamento delle mani** (attivo nel progetto per Android) e gli **fps** reali sul visore.
- Le nuvole sono **pulite** (campionate da mesh esistenti, senza rumore né buchi, normali stimate dal server):
  l'obiettivo reale potrebbe avere scansioni peggiori.
- I materiali generati hanno bisogno di *Render Face = Both* (le facce prodotte non hanno un'orientazione coerente
  per Unity); i modelli producono solo geometria, **nessun colore o texture**.

## 6. Da fare (in ordine suggerito)

1. Recuperare i CSV dal visore (`pull_results.bat`) e confrontare cavo e Wi-Fi: rete, caricamento del `.glb`, fps.
2. Ridurre la coda: priorità agli oggetti centrali o vicini e limite di richieste simultanee; opzionale un backend
   "replay" che restituisce mesh già generate con ritardi realistici, per le dimostrazioni.
3. Provare il backend con un modello vero (e generare la mesh completa di TreeMeshGPT); con il PC del laboratorio
   per i numeri sulla GPU potente.
4. Misurare memoria e qualità con le mesh dei modelli, variando il numero di punti.
5. Confronto **Edge contro inferenza sul Quest** (proposta dello studente, ancora senza risposta della relatrice):
   nel progetto c'è già il pacchetto Unity Inference Engine (`com.unity.ai.inference`).
6. Robustezza: nuvole con rumore e buchi, scena reale.
7. Scrittura della tesi.

**Domande ancora aperte per Claudia**: tempo di attesa accettabile e numero di oggetti per scena; cosa mostrare
nell'attesa (finora: i punti); come gestire colori e texture; confronto Edge contro Quest; accesso al PC del
laboratorio (offerto, "se non si riesce con il tuo").

## 7. Contesto pratico

- Windows 11, Python 3.11 (`.venv`), Unity 6.3 LTS (6000.3.26f1), progetto in `unity/OnDemandMeshVR`, repository
  `plg2001/ondemand-mesh-vr`, ramo `main`. Il disco è stato quasi pieno: tra pesi (10,7 GB), `.venv` (6 GB) e cache
  di Unity (4 GB e in crescita) il progetto supera i 20 GB; su GitHub ne finiscono circa 100 MB.
- Quest 2 con Android 14 (API 34). **Un controller ha lo stick difettoso (deriva)**: mai dipendere dall'analogico;
  si cammina e si gira fisicamente. Il gamepad PS4 è stato considerato e rimandato.
- Il file `unity/OnDemandMeshVR/ProjectSettings/UnityConnectSettings.asset` risulta modificato da Unity (servizi
  attivati) e **non è stato committato di proposito**.
- Documentazione e README in italiano, codice e commenti in inglese. Lo studente non conosceva Unity né i visori:
  spiegare i passaggi uno per uno, nominare ogni file e dire sempre cosa è **verificato** e cosa è solo previsto.

## 8. Comandi utili

```powershell
python -m pytest -q tests                      # 52 test, senza pesi né GPU
start_edge_server.bat                          # server mock per le prove nell'editor
tools\quest\check_headset.bat                  # il Quest è collegato e autorizzato?
tools\quest\server_usb.bat                     # server + collegamento via cavo (prima prova)
tools\quest\server_wifi.bat                    # server raggiungibile dal Wi-Fi
tools\quest\install_app.bat                    # installa Builds\OnDemandMeshVR.apk
tools\quest\pull_results.bat                   # copia i CSV dei tempi dal visore
python data\scenes\build_scene.py              # rigenera la scena di prova
```

In Unity: **Tools → Quest → Prepare and build APK** (la prima build è durata 35 minuti; le successive dovrebbero
essere più brevi, ma non è stato misurato).
