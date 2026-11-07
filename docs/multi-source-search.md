# Standaard zoeken op alle bronnen

## Gedrag

`search_jobs` gebruikt standaard `source=all`. Elke geregistreerde `IJobSource` krijgt dezelfde generieke zoekvraag. Beschikbare adapters: Harvey Nash, Striive/Vertage, Flextender, Freep, MiPublic, Magnit, LibLab, Werken in Gelderland en StarApple. De toepassing zoekt op de bronnen onafhankelijk en geeft per bron een status terug.

- `completed`: deze bronpagina is beschikbaar, eventueel met vervolgcurs or en waarschuwingen.
- `connecting`: de browserpagina wordt nog geladen; de host hervat na kort wachten.
- `login_required`: de host vraagt de gebruiker zelf in te loggen in het aparte ZZP MCP-browservenster. Geen wachtwoorden via chat.
- `error`: echte bronfout, geen succesvolle lege lijst. Andere bronnen kunnen wel resultaten leveren.

Een ontbrekende Vertage-sessie opent automatisch de zichtbare browser met een eigen profiel. De logincheck is een echte beschermde leesactie, vergeleken met dezelfde request zonder sessie. Bestaande bruikbare sessies worden hergebruikt. De MCP-call wacht niet op menselijke login.

De response bevat `searchId`, samengevoegde `jobs`, afzonderlijke `sources` met hun pagina's en `userActions`. De AI-host gebruikt `userActions.message` om de gebruiker om login te vragen. Na de menselijke bevestiging roept de host `resume_search(searchId)` aan; de server controleert technisch of de sessie werkt. Een herhaalde `resume_search` herhaalt alleen onvoltooide bronnen. Parameters en reeds geslaagde bronresultaten blijven behouden. Er kunnen maximaal 32 acties gedurende 30 minuten in geheugen staan; na serverherstart vervallen ze. Er worden geen cv's opgeslagen.

`complete` betekent dat de gevraagde pagina van elke bron beschikbaar is. Het zegt niet dat alle vervolgpagina's of volledige teksten zijn onderzocht. Paginering blijft per bron: dezelfde parameters plus expliciete `source` en de bijbehorende `nextCursor`.

## Vertage-adapter

Lijst: de door de gebruiker aangeleverde GET-route `https://freelancer.vertage.com/api/v2/job-requests`. `search_jobs` gebruikt de generieke paginagrootte en zoekparameters, aangevuld met `maxHoursPerWeek=40` en `useMatchingScore=true`. `collect_jobs` gebruikt de volledige opgegeven query met size=1000, maxRadius=100, lege overige bronfilters en DESCENDING-sortering. Een volle bronpagina krijgt een vervolg, een korte/lege pagina beëindigt de reeks. Het brontotaal blijft onbekend. De collector bewaart voortgang per collectionId en doorloopt ook meer dan 1000 records; de scanlimiet van de generieke search_jobs geldt daar niet. Compleet geldt alleen binnen deze account-/filtergebonden scope. Scans verwijderen nooit gedeelde vacatures, details of eerdere sessieverwijzingen; afwezigheid bewijst geen sluiting.

Live onderzocht: `.net` gaf twee resultaten waar `C#` nul gaf; `locations=Utrecht` gaf vacatures in Utrecht en Huis ter Heide/Utrecht. De native locatie- en radiusparameters worden doorgegeven, maar exacte afstandssemantiek is niet vastgesteld. Zoekmachines behandelen termen verschillend: de AI-host moet zo nodig synoniemen gebruiken. Harvey Nash-categorie-ID's zijn niet geldig bij Vertage; zulke filters worden expliciet afgewezen. Een gezamenlijke zoekactie accepteert geen bronspecifieke categorie-ID's.

Volledige details: bestaande teksten komen uit SQLite. Ontbrekende teksten komen rechtstreeks uit de door de gebruiker opgegeven GET-route `https://freelancer.vertage.com/api/job-requests/{id}`, met de beschermde lokale browsersessie. Browsernavigatie naar een detailpagina is niet nodig. `description`, voorwaarden en gestructureerde eisen worden naar leesbare tekst vertaald. Skills komen uit expliciete `COMPETENCE`-velden; contractcodes bewijzen geen ZZP/payroll-toestemming. Een cachetreffer bewaart de oorspronkelijke ophaaltijd en wordt niet als actuele broncontrole gepresenteerd. Er is geen TTL of automatische verversing.

De native lijst bevat niet alle tekst. Daarom verrijkt Vertage `search_jobs` de treffers met volledige details en levert die ook in `details` naast de generieke samenvattingen. `collect_jobs` bewaart de volledige teksten batchgewijs; lees ze daarna met `get_job`. Detailfouten zijn geen succesvolle lijstresultaten of bewijs van ongeschiktheid. Ophalen/verzamelen betekent niet automatisch inhoudelijk beoordelen.

De bron levert ondertekende afbeeldingslinks en persoonlijke matching-/favorietvelden mee. Die zijn niet nodig voor vacaturematching en worden uit genormaliseerde details/fixtures verwijderd. Ook querytokens in bron-HTML/links worden verwijderd. Sessiecookies en authheaders blijven uitsluitend lokaal in de sessielaag. De fixture `vertage.sanitized.json` bevat alleen benodigde vacaturevelden; geen sessie- of accountgegevens.

## Verificatie op 5 november 2026

- 59 offline tests geslaagd: onder andere gedeeltelijke resultaten, loginactie, hervatten met identieke parameters, niet opnieuw ophalen van geslaagde bronnen, idempotent hervatten, verbindingsfase, sessieverloop tijdens zoeken, echte bronfout, onbekende zoek-ID, Vertage-mapping/paginering en verwijderen van signed links/accountvelden.
- Echte MCP-client ontdekte zeven tools inclusief `resume_search`.
- Eén standaardzoekactie op Utrecht gaf in dezelfde response drie Harvey Nash- en drie Vertage-resultaten, met onafhankelijke vervolgcursors.
- Volledige Vertage-details voor bron-ID `395280` zijn opgehaald: 7365 tekens leesbare tekst.
- Na expliciet uitloggen leverde dezelfde zoekingang de Harvey Nash-pagina plus `vertage: login_required` en een concrete `userAction` op.
- Na menselijke herlogin werd dezelfde bewaarde `searchId` hervat: eerst `connecting`, daarna beide bronnen `completed`, geen nieuwe loginactie en de eerdere Harvey Nash-resultaten behouden.
- De nieuwe server is in Release via de testclient geverifieerd. Een al aangesloten oude MCP-server moet worden gestopt/herstart en zijn toolcatalogus opnieuw laden voordat deze chat de nieuwe standaard en extra tool ziet.

Reproductie (na build):

```powershell
$env:ZZPMCP_BROWSER_CHANNEL='chrome'
dotnet run --project tools/ZzpMcp.Smoke -c Release --no-build -- --all-live E:/source/zzp-mcp/src/ZzpMcp/bin/Release/net10.0/ZzpMcp.dll
```

Interactieve commando's: `new` start opnieuw over alle bronnen, `resume` hervat de bewaarde actie, `detail vertage <id>` leest volledige tekst, `exit` sluit af. De tool meldt onvolledige verificatie als bij afsluiten nog bronnen ontbreken.

## Flextender

De publieke website levert alle client-side lijstpagina's via één Ajax-response. Volledige teksten en provincie worden opgehaald; bestaande details komen uit SQLite. Gebruik `collect_jobs(source="flextender", sessionId=...)` voor brede verzameling. Een locatievraag geeft voor deze bron `unsupported_filter`; plaats-/kilometersemantiek is niet geverifieerd. De ingelogde leveranciersomgeving wordt niet gebruikt. Zie [broncontract en verificatie](flextender.md).

## Vijf aanvullende openbare bronnen

Freep, MiPublic, Magnit, LibLab, Werken in Gelderland en StarApple ondersteunen brede verzameling via `collect_jobs`. Ze gebruiken anonieme HTTP, volledige details en de gedeelde detailcache. Bij `search_jobs` wordt een ingevulde `query` lokaal en letterlijk toegepast op titel, opdrachtgever en volledige tekst van de huidige bronpagina. `localMatchTotal=null` is geen nulresultaat; blijf vervolgpagina's doorlopen. `appliedLocalFilters` beschrijft de scope en `appliedSourceFilters` claimt geen native zoekterm. `location` en categorie-ID's worden expliciet afgewezen. Zie [broncontracten, limieten en verificatie](public-source-adapters.md).
