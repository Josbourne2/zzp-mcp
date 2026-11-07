# ZZP MCP

Lokale MCP-server voor vacatures en opdrachten. C#/.NET 10, stdio, officiële MCP C# SDK 2.3.1. **Harvey Nash, Striive/Vertage, Flextender, Freep, MiPublic, Magnit, LibLab, Werken in Gelderland en StarApple** zijn zoekadapters. Eén zoekvraag doorzoekt standaard alle adapters. Waar login ontbreekt, opent de server een apart browserprofiel en geeft hij een concrete loginactie terug aan de AI-host. Matching met cv en zoekvraag gebeurt bij de AI-host.

## Meegeleverde skill

[zzp-jobsearch](skills/zzp-jobsearch/SKILL.md) beschrijft de agentwerkwijze: breed verzamelen op rolgebieden, volledige vacatureteksten onderzoeken en daarna inhoudelijk matchen. Samenvattingen kunnen het onderzoek ondersteunen, met behoud van de volledige tekst en bronverwijzingen. De server verzamelt en ontsluit; de agent analyseert en selecteert.

De skill wordt als bronbestand in deze repository meegeleverd. Een MCP-verbinding laadt of installeert haar niet automatisch. Gebruik het bestand als instructie voor de agent, of installeer de map `skills/zzp-jobsearch` op de skilllocatie van de gekozen host. SQLite bewaart nu zoekpagina's, bronstatussen, vacatures en detailversies van de bestaande adapters. Batchgewijze corpusontsluiting, lokale tekstzoekfunctie en eventuele opslag van agentsamenvattingen zijn vervolgwerk uit het [vervolgplan](AI/plans/sqlite-en-bronuitbreiding.md).

## Starten en testen

Benodigd: .NET 10 SDK; voor de optionele browserproef Microsoft Edge of Chrome en een eigen Striive/Vertage-account. `global.json` kiest een stabiele .NET 10 SDK. Getest met 10.0.401 op Windows. Pakketten zijn vastgelegd in projectbestanden en `packages.lock.json`.

Voer vanuit de repository uit:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-restore
dotnet run --project tools/ZzpMcp.Smoke --no-build
dotnet run --project tools/ZzpMcp.Smoke --no-build -- --config examples/stdio-client.json --live
```

De eerste smokecheck gebruikt een echte MCP-client zonder bronrequests. `--live` controleert Harvey Nash, paginering, details vanuit een nieuw serverproces, lege resultaten en filters. `--all-live` controleert zoeken over beide bronnen met een interactieve `resume` na menselijke login; gebruik hierbij dezelfde DLL-keuze als de gebouwde configuratie. De xUnit-tests gebruiken uitsluitend fixtures/fakes. Live aantallen kunnen veranderen; een fout beëindigt de smokecheck met een niet-nul exitcode.

Start de server via een lokale MCP-client met:

```powershell
dotnet E:/source/zzp-mcp/src/ZzpMcp/bin/Debug/net10.0/ZzpMcp.dll
```

Dit proces spreekt MCP op stdin/stdout en wacht op de client. Alle logs gaan naar stderr. Gebruik de gebouwde DLL, zodat buildoutput niet in het protocol terechtkomt.

Sluit een actieve server voordat je dezelfde buildconfiguratie opnieuw bouwt: Windows vergrendelt de geladen DLL. Het lokale profiel blijft na normaal afsluiten behouden.

## Clientconfiguratie

De meegeleverde, geteste client is `tools/ZzpMcp.Smoke`, eveneens MCP SDK 2.3.1. Die start de server met de `Command` en `Arguments` uit [het lokale configuratievoorbeeld](examples/stdio-client.json). Andere clients kunnen dezelfde stdio-configuratie gebruiken; pas het absolute DLL-pad aan. De eerdere versie is ook via de aangesloten `zzp_mcp`-tools in deze chat gebruikt voor bronnen, zoeken en details. Een cloudhost kan deze lokale server/browser niet vanzelf bereiken. **Na een update moet de MCP-client de server herstarten en de tools opnieuw ontdekken**; de nieuwe standaard en `resume_search` zitten niet in een nog draaiende oude server.

```json
{
  "mcpServers": {
    "zzp-mcp": {
      "command": "dotnet",
      "args": ["E:/source/zzp-mcp/src/ZzpMcp/bin/Debug/net10.0/ZzpMcp.dll"]
    }
  }
}
```

## Registreren in ChatGPT Desktop

Volgens de [officiële OpenAI MCP-handleiding](https://learn.chatgpt.com/docs/extend/mcp) ondersteunt de desktop-app lokale stdio-servers via **Settings → MCP servers → Add server**. Vul naam `zzp-mcp`, type `STDIO`, commando `dotnet` en als enig argument het absolute DLL-pad uit het voorbeeld in. Bewaar met **Save**, kies **Restart** en controleer de verbinding met `/mcp`. Zet voor Chrome de serveromgevingsvariabele `ZZPMCP_BROWSER_CHANNEL=chrome`.

De desktop-app, Codex CLI en IDE-extensie delen volgens die handleiding de MCP-configuratie van dezelfde lokale host. Je kunt daarom ook de inhoud van [examples/chatgpt-desktop.toml](examples/chatgpt-desktop.toml) toevoegen aan `%USERPROFILE%/.codex/config.toml`, zonder andere instellingen te vervangen. Pas het DLL-pad aan indien de repository elders staat. De instellingshandelingen zelf zijn niet geobserveerd; de vorige serverversie is daarna wel succesvol vanuit deze chat aangeroepen.

Bij een nieuwe zoektocht maakt de assistent met `start_session` een onderzoekssessie aan en geeft de teruggegeven `sessionId` mee aan `search_jobs` en `get_job`. Bij voortzetten van eerder onderzoek gebruikt hij `list_sessions`/`get_session`. Zoek standaard zonder bronkeuze. De server doorzoekt alle adapters en opent zo nodig de Vertage-browser. Bij `userActions` vraagt de assistent om handmatige login, daarna roept hij `resume_search(searchId)` aan. Bij `connecting` hervat hij na kort laden. Harvey Nash hoeft niet op menselijke login te wachten. Sluit de losse browser-smokeclient voordat dezelfde browserprofielmap vanuit ChatGPT wordt geopend.

## Tooloverzicht

| Tool | Gebruik |
| --- | --- |
| `list_sources` | Werkende adapters, verbindingswijze en zoekmogelijkheden. `configured_not_health_checked` is geen live gezondheidsclaim. |
| `collect_jobs` | Brede lijsten voor Vertage, Flextender, Freep, MiPublic, Magnit, LibLab, Werken in Gelderland en StarApple; daarna alleen ontbrekende details in hervatbare batches. Scans verwijderen nooit gedeelde vacatures, details of historie. |
| `start_session` | Nieuwe onderzoekssessie met eigen `sessionId` en leeg overzicht; bestaande vacaturehistorie blijft behouden. |
| `list_sessions` | Opgeslagen onderzoekssessies terugvinden, nieuwste eerst. |
| `get_session` | Voortgang na herstart: vacatures/versies (`section=jobs`) of bronpagina's en openstaand werk (`section=pages`), begrensd met `nextAfterId`. |
| `mark_job_reviewed` | Exacte via `get_job` opgehaalde `versionId` na inhoudelijk lezen als verwerkt markeren in deze sessie. |
| `search_jobs` | Standaard `source=all`: alle bronnen, gedeeltelijke resultaten, bronstatussen en eventuele loginacties. Een expliciete bron geeft een enkele bronpagina. |
| `resume_search` | Hervat met alleen `searchId` de oorspronkelijke zoekvraag; alleen onvoltooide bronnen worden opnieuw geprobeerd. |
| `get_job` | Volledige tekst uit SQLite indien aanwezig (`cacheUsed=true`); alleen ontbrekende details komen van de bron. Behoudt de oorspronkelijke ophaaltijd. |
| `browser_connect` | Open eigen lokaal browserprofiel; keert terug voordat de gebruiker inlogt. |
| `browser_status` | Echte beschermde GET met sessie en zonder sessie; sessiestatus zonder persoonsgegevens. |
| `browser_close` | Browser sluiten, lokaal profiel behouden. Dit logt niet uit. |

Voorbeeldzoekactie:

```json
{"query":"C#","location":"Utrecht","radiusKm":25,"pageSize":20}
```

Een zoekactie op alle bronnen bevat `searchId`, samengevoegde `jobs`, `sources` met afzonderlijke pagina's en `userActions`. `complete` betekent dat alle gevraagde bronpagina's beschikbaar zijn, niet dat alle vervolgpagina's zijn onderzocht. Ontbrekende login is een concrete actie, een bronfout blijft een fout. Hervatten bewaart oorspronkelijke parameters en eerder geslaagde bronresultaten maximaal 30 minuten in geheugen; na serverherstart is opnieuw zoeken nodig.

Een `sessionId` identificeert het duurzame onderzoek; een `searchId` alleen een afzonderlijke zoekactie. Na serverherstart haalt `get_session` de opgeslagen voortgang terug, waarna nieuwe zoekacties met dezelfde `sessionId` kunnen volgen. De server herkent chatgrenzen niet automatisch en heeft geen globale actieve sessie. Zonder `sessionId` blijven bestaande clients werken, maar hun acties worden niet achteraf aan een onderzoekssessie toegewezen. Een nieuwe sessie, zoekscan of verzamelscan verwijdert geen gedeelde vacatures, details of eerdere sessieverwijzingen. Zo blijven .NET- en securityzoektochten naast elkaar bestaan. Zie [sessies en voorbeelden](docs/sqlite-storage.md#onderzoekssessies).

Vraag vervolgpagina's **per bron** met `source`, dezelfde zoekparameters en de bijbehorende `nextCursor`. Ook bij nul lokale treffers kan er een vervolg zijn. `sourceTotal` telt bronresultaten vóór lokale filtering; Vertage publiceert via deze route geen vastgesteld totaal en geeft `null`. Een volle Vertage-pagina heeft een vervolg; een korte/lege pagina beëindigt die reeks. Onbekende filters worden afgewezen. Categorie-ID's zijn uitsluitend voor expliciete bronzoekacties. Bron- en lokale voorwaarden staan afzonderlijk in de response.

Paginagrootte 1–100; maximaal offset 999, HTTP-timeout 20 seconden, detailactie maximaal 60 seconden en 1000 terugzoekrecords. Bronresponse maximaal 10 MiB, toolresponse 2 MiB. Een overschrijding is een expliciete fout, nooit stil afgekapt detail. Offsetpaginering is geen snapshot: bronwijzigingen kunnen overlap of omissies geven. Dedupliceer vervolgpagina's bij de host op `(source, sourceId)`.

## Model en bronadapter

De database staat standaard buiten de repository in `%LOCALAPPDATA%/ZZPMcp/jobs.db`. Stel `ZZPMCP_DATABASE_PATH` in voor een ander bestand. Zoeken leest de bronlijst; volledige details worden hergebruikt uit SQLite en alleen opgehaald als ze ontbreken. `cacheUsed=true` betekent een historische lokale tekst met oorspronkelijke ophaaltijd, geen actuele bron- of logincontrole. Er is geen TTL/automatische detailverversing. `collect_jobs` doorloopt de Vertage-lijst met size=1000 en haalt ontbrekende details op in hervatbare batches. Iedere verzameling bewaart haar eigen waargenomen leden. Ook een complete of lege scan verwijdert geen gedeelde vacatures, detailversies, zoekhistorie of sessieverwijzingen. Bewaartermijnen en expliciete opschoning zijn afzonderlijk vervolgwerk. `storage.state` meldt `saved` of `failed` afzonderlijk van het bronresultaat. Ruwe payloads en browsergegevens worden niet opgeslagen. Zie [SQLite-opslag en beheer](docs/sqlite-storage.md) voor scope, versies en backup.

`SearchRequest`, `Job`, `JobSummary` en `IJobSource` zijn het gemeenschappelijke formaat; Harvey Nash is geen canoniek schema voor toekomstige bronnen. `JobService` coördineert zoeken en cursors. De adapter vertaalt native API-velden, valideert bronresponses en normaliseert hun betekenis. De MCP-laag bevat geen concrete bronadapter. Een nieuwe bron implementeert `IJobSource` en wordt in DI geregistreerd; er zijn geen lege adapters voor latere bronnen.

Onbekende feiten blijven null/leeg of `unknown`. ZZP en payroll zijn onafhankelijk (`allowed`, `not_allowed`, `unknown`) en bevatten bronbewijs. `Interim`, detachering of algemene zelfstandigenvoorwaarden bewijzen geen toestemming. Tegenstrijdig expliciet bewijs levert `unknown` + conflictwaarschuwing. Harde `allowed`-filters sluiten onbekenden/conflicten uit.

Bedragen behouden tekst, soort, valuta, eenheid en voorwaarden voor zover aantoonbaar. Numerieke nulvelden zijn geen aangeboden nultarief. Een ‘richttarief’ zonder expliciete tijdseenheid krijgt geen verzonnen ‘per uur’. Eisen/vaardigheden komen alleen uit herleidbare bronvelden of herkenbare kopjes. Lees altijd de volledige tekst; lege gestructureerde lijsten bewijzen geen afwezigheid. Vertage-lijsttreffers bevatten nog niet alle tekst: gebruik `get_job` vóór matching. Ook `typeOfProfessional` blijft als broncode bewaard zonder payroll/ZZP-toestemming te raden. Bron-HTML wordt omgezet met paragrafen, lijsten en hyperlinks; scripts/styles worden niet uitgevoerd. Er is geen cv-opslag.

## Browserproef

```powershell
dotnet run --project tools/ZzpMcp.Smoke --no-build -- --browser
```

Standaard wordt Edge gebruikt. Voor Chrome:

```powershell
$env:ZZPMCP_BROWSER_CHANNEL='chrome'
dotnet run --project tools/ZzpMcp.Smoke --no-build -- --browser
```

Bij een andere MCP-client zet je `ZZPMCP_BROWSER_CHANNEL` in de serveromgeving (bijvoorbeeld `"env": {"ZZPMCP_BROWSER_CHANNEL":"chrome"}` naast `command` en `args`). Alleen `msedge` en `chrome` zijn toegestaan. Beide gebruiken een eigen ZZP MCP-profiel; bestaande gewone browsertabs/sessies worden niet overgenomen.

Log zelf in bij `freelancer.vertage.com`. De proef opent de opdrachtenroute `/inbox`; als login op het dashboard eindigt, navigeert een volgende statuscontrole zelf naar de opdrachtenlijst. Typ in de terminal `status` voor een korte controle, `restart` om het lokale profiel opnieuw te openen, `exit` om af te sluiten. De MCP-startcall wacht niet op menselijke login. De client blijft wel actief, zodat de browser bij hetzelfde serverproces blijft horen. Afsluiten zonder bevestigde beschermde leesactie geeft exitcode 2 in plaats van een succesmelding.

De proef observeert alleen een bestaande GET naar `https://freelancer.vertage.com/api/v2/job-requests`, bewaart URL en benodigde headers uitsluitend in geheugen en controleert dezelfde leesactie zonder sessie. `connected` vereist geldige JSON met sessie en een loginrespons zonder sessie. Na de eerste succesvolle controle blijft de oorspronkelijke request-URL bewaard voor herhaling na herlogin; verse headers worden opnieuw uit de browser waargenomen. De probe leest geen wachtwoord en logt geen cookies, headers of inhoud. Hij schrijft geen cv naar een bron.

Profiel: `%LOCALAPPDATA%/ZZPMcp/striive-profile` (Edge) of `striive-chrome-profile` (Chrome), buiten de repository. Gebruik één serverproces tegelijk per profiel. Sluiten behoudt het profiel; uitloggen gebeurt in de site. Automatische MFA en login zonder browser zijn niet gebouwd. De interne routes zijn geen officiële API-garantie. Op gebruikersverzoek gebruikt Vertage `useMatchingScore=true` en `maxHoursPerWeek=40`; de volledige verzamelroute gebruikt ook `maxRadius=100` en size=1000. Dit definieert de account-/filtergebonden verzamelscope, geen universele inventaris van de site. Afwezigheid uit die lijst veroorzaakt geen verwijdering uit de gedeelde database. Exacte afstandssemantiek is niet vastgesteld. Signed links en persoonlijke account-/matchingvelden worden uit details en fixtures verwijderd.

Zie [zoeken op alle bronnen en live bewijs](docs/multi-source-search.md), [API-bevindingen](docs/harvey-nash-api.md), [AI-hostworkflow](docs/host-workflow.md), [verificatieverslag](docs/verification.md) en [plan met precies vijf reviews](AI/plans/zzp-mcp.md).

Een persoonlijk voorbeeld met het actuele zoekprofiel en cv van Jos staat in [examples/jobsearch](examples/jobsearch/README.md). Verwijs naar die bestanden bij een nieuwe zoekvraag; de MCP-server ontvangt alleen de daarvan afgeleide zoekparameters.

Flextender gebruikt de publieke website zonder login, inclusief volledige teksten en provincie. Locatie-/kilometerfilters worden expliciet afgewezen. Zie [Flextender](docs/flextender.md) voor de bronroute, beperkingen en verificatie.

De zes [openbare bronadapters](docs/public-source-adapters.md) gebruiken geen browser of login. `query` filtert lokaal op de volledige teksten van alleen de opgehaalde pagina: vervolg `nextCursor` ook bij nul treffers. Kilometerfilters zijn niet vastgesteld en worden expliciet afgewezen. Magnit heeft wereldwijd aanbod; MiPublic verzamelt alle vakgebieden.
