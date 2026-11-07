# SQLite-opslag

De eerste implementatiestap van 5 november 2026 sluit de bestaande Harvey Nash- en Vertage-adapters aan op duurzame SQLite-opslag. Nieuwe adapters worden pas gebouwd na de afgesproken gezamenlijke inspectie van hun zoek- en detailflow.

## Configuratie en schema

Standaardbestand: `%LOCALAPPDATA%/ZZPMcp/jobs.db`. De omgevingsvariabele `ZZPMCP_DATABASE_PATH` kiest een ander bestand, ook voor de smokeclient. Bovenliggende mappen worden aangemaakt. Gebruik voor betrouwbare lokale werking een bestand op een lokale schijf.

`PRAGMA user_version` bevat schemaversie 3. Initialisatie en migratie vanaf versie 1/2 gebeuren atomair, met behoud van bestaande gegevens. Oude zoekacties blijven zonder onderzoekssessie staan. Een nieuwere onbekende versie wordt afgewezen zonder terugzetten. Iedere verbinding gebruikt foreign keys en een busy-timeout van vijf seconden. Schrijftransacties omvatten geen netwerkrequests. Verbindingen worden na iedere operatie gesloten; pooling staat uit. De standaard SQLite-journalmodus wordt behouden.

| Tabel | Inhoud |
| --- | --- |
| `sources` | Bron- en portaalidentiteit; bronnen zonder portaal gebruiken de lege string. |
| `jobs` | Unieke `(source, portal, source_id)`, genormaliseerd lijstrecord inclusief eventuele snippet, actuele samenvatting, eerste/laatste waarneming en afzonderlijke lijst-/detailtijd. |
| `job_versions` | Genormaliseerde volledige vacature, SHA-256-inhoudshash en tijdstip van de eerste waarneming van die versie. |
| `searches` | Zoek-ID, generieke vraag, starttijd en optionele onderzoekssessie. |
| `search_pages` | Afzonderlijke bronpogingen met vraag/cursor, status, foutcode, filters, tellingen, vervolg en waarschuwingen. |
| `search_hits` | Relatie tussen pagina en vacature met volgorde en resultaat van lokale filtering. |
| `research_sessions` | Duurzaam sessie-ID, optionele naam en aanmaaktijd. |
| `session_jobs` | Unieke sessie/vacature-relatie, stabiele lid-ID en toevoegmoment. |
| `session_reads` | Exacte via `get_job` opgehaalde versie per sessie/vacature, ophaalmoment en optionele expliciete verwerkingsmarkering. |
| `collection_runs` | Duurzame scan binnen bronvoorwaarden, sessie, bron/portaal, volgende pagina en status. Het historische veld `removed_jobs` blijft bestaan voor compatibiliteit; scans verhogen het niet meer. |
| `collection_members` | Bron-ID's in de lijstscan, detailfoutcode en laatste detailpoging. |

JSON bewaart de genormaliseerde modellen en filterinformatie; identiteiten en zoekrelaties hebben relationele sleutels en constraints. Portaalidentiteit is beschikbaar in `IJobSource.PortalId`; beide huidige adapters gebruiken de standaardwaarde. De database voegt nooit verschillende bronnen of portalen samen op titel.

## Schrijfgedrag

`search_jobs` blijft de bron raadplegen. Alle geretourneerde bronrecords worden opgeslagen, ook als een expliciet lokaal contractfilter ze uit de response verwijdert. Een zoekactie op alle bronnen en latere `resume_search`-pogingen delen één zoek-ID. Losse bronpagina's krijgen ieder een eigen zoek-ID, zichtbaar in `storage.searchId`; het meegegeven cursor blijft in de pagina bewaard. Opnieuw ophalen van dezelfde pagina voegt een waarneming toe, geen tweede vacature.

Harvey Nash levert volledige tekst in de lijst. De native Vertage-lijst levert samenvattingen; Vertage `search_jobs` verrijkt alle treffers met volledige details, uit SQLite indien aanwezig en anders via `/api/job-requests/{id}`. De response bevat die teksten in `details`. `get_job` hergebruikt bestaande volledige details en haalt alleen ontbrekende details bij de bron op. `cacheUsed=true` en de oorspronkelijke `fetchedAt` maken de historische status zichtbaar. Zo'n lokale leesactie doet geen bronrequest en claimt geen actuele login, beschikbaarheid of ongewijzigde inhoud. Op uitdrukkelijk gebruikersverzoek is er geen TTL of automatische detailverversing. includeRaw kan alleen bij een live miss ruwe velden opleveren; de cache bewaart die niet.

Een lijstrecord vervangt nooit een volledig detailrecord. Een detailversie ontstaat alleen als de genormaliseerde inhoud verandert. `FetchedAt`, ruwe payloads en opslagmetadata tellen niet mee voor de hash. Opnieuw waarnemen van dezelfde inhoud werkt alleen `detail_fetched_at` bij. Een inhoudelijke terugkeer van A naar B naar A bewaart drie opeenvolgende versies. Een oudere, later ontvangen response vervangt geen recenter detail. De actuele leestijd komt uit `jobs.detail_fetched_at`; `job_versions.fetched_at` blijft de eerste ophaaltijd van de betreffende versie.

Ruwe bronpayloads worden geheel weggelaten. Browserprofielen, cookies, authheaders, cv's en inhoudelijke agentbeoordelingen horen niet in deze database. De sessie bewaart alleen een expliciete markering dat een tekstversie is verwerkt. Normalisatie en bestaande bronsanering blijven verantwoordelijkheid van de adapters.

## Onderzoekssessies

Een onderzoekssessie staat los van browserlogin, MCP-transport en de levensduur van het serverproces. Chatgrenzen worden niet automatisch herkend. De assistent start voor nieuw onderzoek expliciet een sessie en geeft de ID mee aan alle bijbehorende zoek- en detailacties. Er bestaat geen globale actieve sessie: gelijktijdige gesprekken gebruiken elk hun eigen ID. `sessionId` blijft voor oudere clients optioneel; acties zonder ID blijven buiten onderzoekssessies.

Voorbeeldtoolaanroepen (vervang de voorbeeld-ID door de teruggegeven ID):

```json
{"name":"start_session","arguments":{"name":"Ontwikkel- en architectuurrollen"}}
{"name":"search_jobs","arguments":{"query":"developer","sessionId":"<sessionId>"}}
{"name":"get_job","arguments":{"source":"vertage","sourceId":"123","sessionId":"<sessionId>"}}
{"name":"mark_job_reviewed","arguments":{"sessionId":"<sessionId>","source":"vertage","sourceId":"123","versionId":42}}
```

`versionId` komt uit `get_job.storage.versionId`, na controle van `storage.state=saved`. Alleen ophalen markeert nooit automatisch als inhoudelijk verwerkt. Een beoordeling kan alleen verwijzen naar een versie die via `get_job` in die sessie is opgehaald, en blijft aan die exacte versie gekoppeld. Een nieuwere inhoudsversie maakt de huidige vacature opnieuw onbeoordeeld. Lijstresultaten mogen details in de database vullen, maar tellen niet als gelezen detail in de onderzoekssessie. Bij een verouderde detailresponse die niet als versie is opgeslagen, blijft `versionId` leeg; er wordt dan geen gelezen versie verzonnen.

Een nieuwe sessie krijgt een nieuwe ID en een leeg onderzoeksoverzicht. Er worden geen vacatures of eerdere sessies verwijderd. Dezelfde vacature kan bij meerdere sessies horen zonder duplicatie van de bronidentiteit of overname van elkaars lees-/beoordelingsvoortgang. Sessies zijn een onderzoeksindeling binnen één lokale database, geen toegangsbeveiliging tussen gebruikers.

Na een herstart:

```json
{"name":"list_sessions","arguments":{"pageSize":20}}
{"name":"get_session","arguments":{"sessionId":"<sessionId>","section":"jobs","pageSize":20}}
{"name":"get_session","arguments":{"sessionId":"<sessionId>","section":"pages","pageSize":20}}
```

`get_session` toont aantallen verzamelde vacatures, vacatures met een in de sessie opgehaalde detailversie, momenteel verwerkte versies, zoekacties en openstaande pagina's. De sectie `jobs` geeft bronidentiteiten, portalen, samenvattingen en versie-ID's. De sectie `pages` geeft bronpogingen met parameters, foutcodes, cursors en `pending`. Beide secties pagineren onafhankelijk op stabiele oplopende rij-ID's: gebruik `nextAfterId` als `afterId`. Nieuw toegevoegde leden krijgen een hogere ID; weergegeven actuele velden kunnen door verversing veranderen. Dit is geen onveranderlijke corpus-snapshot. `list_sessions` gebruikt begrensde offsetpaginering, nieuwste eerst; gelijktijdige toevoegingen kunnen de offsets verschuiven.

Een fout blijft open tot een latere geslaagde poging met dezelfde bron, portaal, parameters en invoercursor. Een vervolg blijft open tot die cursor met dezelfde parameters is opgehaald. Een geslaagde poging in een andere onderzoekssessie of met een andere zoekvraag sluit het werk niet. Onvolledige pagina's blijven zichtbaar, ook zonder vervolgcursor. Herhaal voor fouten/incompleetheid de opgeslagen `request`; vervolg een geslaagde pagina met zijn `nextCursor` en dezelfde sessionId. Blijvende bronlimieten moeten expliciet gerapporteerd worden, niet eindeloos opnieuw geprobeerd.

Dit hervat onderzoek, niet een oude `resume_search`-actie: die blijft 30 minuten geldig in het oorspronkelijke proces. Historische sessievoortgang doet geen bronrequest. Volledige teksten zijn lokaal leesbaar met get_job. Inhoudelijke samenvattingen opslaan en meerdere volledige teksten in één batch exporteren blijven vervolgwerk.

## Vertage volledig verzamelen en synchroniseren

```json
{"name":"collect_jobs","arguments":{"sessionId":"<sessionId>","source":"vertage","batchSize":10}}
{"name":"collect_jobs","arguments":{"sessionId":"<sessionId>","source":"vertage","collectionId":"<collectionId>","batchSize":10}}
```

Bewaar de teruggegeven collectionId en herhaal de tweede aanroep tot complete=true. Iedere aanroep leest maximaal één bronpagina en haalt maximaal 1–20 ontbrekende volledige details op. Bestaande volledige details worden nooit opnieuw aangevraagd. De eerste lijstquery is precies de door de gebruiker opgegeven route:

```text
/api/v2/job-requests?page=0&size=1000&maxRadius=100&sortBy=&sortOrder=DESCENDING&clientNames=&professionalTypes=&remoteAllowed=&locations=&skills=&maxHoursPerWeek=40&useMatchingScore=true
```

Bij een volle pagina volgt page=1 enzovoort, ook voorbij 1000 records. Een korte/lege pagina sluit de lijst af. Detailmisses gebruiken rechtstreeks `/api/job-requests/{id}`, zonder browsernavigatie. Per detail blijft succesvolle opslag bij onderbreking behouden; foutcodes worden opgeslagen en ongeteste ID's krijgen bij een volgende batch voorrang op eerder mislukte details. Lijst- en detailvoortgang blijven na herstart beschikbaar. Zonder collectionId wordt een bestaande onvoltooide verzameling van dezelfde sessie/bron hervat; na voltooiing start een aanroep zonder ID een nieuwe scan.

**Gedeelde opslag behouden:** zoek- en verzamelscans verwijderen nooit vacatures uit de gedeelde database. Dat geldt ook voor een complete of lege lijstscan. Afwezigheid betekent alleen dat een vacature niet is waargenomen binnen de bronvoorwaarden van deze scan; het bewijst geen sluiting of afwezigheid bij een andere zoekvraag of gebruiker. Vacatureteksten, inhoudsversies, zoektreffers, sessielidmaatschappen en lees-/verwerkingsmarkeringen blijven behouden. Iedere collectie bewaart haar eigen ledenlijst. Een .NET-verzameling en een Security Officer-verzameling kunnen zo naast elkaar blijven bestaan. Lijstpagina, scanvoortgang en toegevoegde leden worden atomair opgeslagen. Een rollback bevestigt geen gedeeltelijke voortgang.

Een incomplete, overlappende of herhaalde lijstscan rapporteert onvolledigheid. Detailfouten maken de detaildekking onvolledig. Een nieuwere complete scan uit een andere sessie blokkeert het verder ophalen van ontbrekende details in een oudere verzameling niet. De bestaande beperking van maximaal één gelijktijdige lijstscan per bron/portaal blijft gelden; detailvoortgang is per collectie gescheiden.

De opgegeven URL bevat maxHoursPerWeek=40 en useMatchingScore=true. Compleet betekent dat de lijst binnen die account-/filtergebonden scope en de bijbehorende details zijn verwerkt; dit is geen universele broninventaris. Offsetpaginering is geen onveranderlijke snapshot. `removedJobs` blijft voor nieuwe collecties nul; oude collecties kunnen een historische verwijdertelling behouden. De schemawijziging is niet nodig en eerder verwijderde gegevens worden hiermee niet hersteld. Counters en opgeslagen details zijn geen bewijs dat de agent de volledige teksten inhoudelijk heeft gelezen: lees iedere tekst via get_job en markeer pas daarna de exacte versie.

collect:-cursors in get_session(section=pages) horen bij collect_jobs en mogen niet worden doorgegeven aan search_jobs. Hervat ze met dezelfde collectionId. De details worden individueel met get_job gelezen; lokale FTS en volledige-tekstexport blijven vervolgwerk.

## Fouten en dekking

De pagina, vacatures, versies en zoekrelaties worden atomair opgeslagen. `storage.state=saved` volgt pas na commit. Bij een opslagfout blijven live bronresultaten beschikbaar met `storage.state=failed` en een expliciete melding. `complete` beschrijft de brondekking, geen garantie dat opslag lukte. `get_job` meldt opslagstatus op dezelfde manier.

Herstelbare loginstatussen en `SourceException`-bronfouten worden als afzonderlijke zoekpogingen opgeslagen. Als ook die registratie mislukt, vermeldt de bronfout dat. Ongeldige toolvragen worden vóór bronraadpleging afgewezen en leveren geen zoekhistorie. Geannuleerde of abrupt beëindigde requests hebben nog geen gegarandeerde historische poging: deze versie registreert afgeronde bronpogingen, geen duurzame werkqueue.

Zoekhistorie maakt RAM-resume niet duurzaam: de bestaande 30 minuten en procesgrens blijven gelden. Afwezigheid uit een zoek- of verzamelscan veroorzaakt geen verwijdering uit de gedeelde opslag. Expliciete opschoning en een bewaarbeleid zijn afzonderlijk vervolgwerk; er is geen achtergrondtimer of verborgen bewaartermijn.

## Backup en herstel

Stop alle serverprocessen die dit bestand gebruiken, en kopieer het databasebestand samen met eventueel aanwezige journal-, WAL- en SHM-bestanden. Gebruik voor een backup tijdens actief gebruik een SQLite-backupfunctie in een databasebeheerprogramma; een losse bestandskopie tijdens writes is geen gecontroleerde backup. Herstel bij gestopte server naar het ingestelde pad en bewaar een origineel exemplaar. Controleer zo nodig met `PRAGMA integrity_check` en `PRAGMA foreign_key_check` in een SQLite-client. Databasebestanden en journals worden door git uitgesloten.

## Verificatie en vervolg

Offline tests controleren heropenen, bron-/portaalidentiteit, foreign keys, inhoudsversies, uitgestelde responses, behoud van volledige details, meerdere zoekrelaties, filters, login/resume, gelijktijdige writes, rollback en afwijzen van een nieuwer schema. Sessietests controleren scheiding, behoud van historie, versiegebonden verwerking, herstartbare voortgang, pending pagina's, migratiebehoud en migratierollback. De smokeclient gebruikt standaard een tijdelijke database (of expliciet `ZZPMCP_DATABASE_PATH`) en controleert sessietools met een tweede serverproces zonder live bronrequests.

Aanvullende collectietests controleren ontbrekende-details-only, behoud van gedeelde vacatures/details/historie bij complete en lege scans, behoud van .NET-gegevens en leesvoortgang na een securityscan, hervatten van een oudere collectie na een andere sessiescan, foreign keys, batchhervatting, herstel na de eerste requestfout, migratie en atomair vastleggen van scanvoortgang. De gewijzigde Vertage-routes en query zijn offline gecontroleerd met fixtures/fakes; dit is geen live bewijs voor het huidige broncontract.

Vervolgfasen zijn lokaal zoeken met FTS5, volledige-tekstbatches/export, uitgebreid herkomstbewijs en mogelijke dubbelen. Agentafleidingen blijven afzonderlijk vervolgwerk. Nieuwe adapters verschijnen pas na inspectie en implementatie.
