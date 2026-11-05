# ZZP MCP

Lokale MCP-server voor vacatures en opdrachten. C#/.NET 10, stdio, officiële MCP C# SDK 2.3.1. **Harvey Nash en Striive/Vertage** zijn zoekadapters. Eén zoekvraag doorzoekt standaard alle adapters. Waar login ontbreekt, opent de server een apart browserprofiel en geeft hij een concrete loginactie terug aan de AI-host. Matching met cv en zoekvraag gebeurt bij de AI-host.

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

Na verbinding roept de assistent `search_jobs` aan zonder bronkeuze. De server doorzoekt alle adapters en opent zo nodig de Vertage-browser. Bij `userActions` vraagt de assistent om handmatige login, daarna roept hij `resume_search(searchId)` aan. Bij `connecting` hervat hij na kort laden. Harvey Nash hoeft niet op menselijke login te wachten. Sluit de losse browser-smokeclient voordat dezelfde browserprofielmap vanuit ChatGPT wordt geopend.

## Tooloverzicht

| Tool | Gebruik |
| --- | --- |
| `list_sources` | Werkende adapters, verbindingswijze en zoekmogelijkheden. `configured_not_health_checked` is geen live gezondheidsclaim. |
| `search_jobs` | Standaard `source=all`: alle bronnen, gedeeltelijke resultaten, bronstatussen en eventuele loginacties. Een expliciete bron geeft een enkele bronpagina. |
| `resume_search` | Hervat met alleen `searchId` de oorspronkelijke zoekvraag; alleen onvoltooide bronnen worden opnieuw geprobeerd. |
| `get_job` | Bron + bron-ID: volledige tekst, herleidbare feiten, contractbewijs en tijdstempels. `includeRaw` is standaard false. Werkt zonder eerdere zoekactie. |
| `browser_connect` | Open eigen lokaal browserprofiel; keert terug voordat de gebruiker inlogt. |
| `browser_status` | Echte beschermde GET met sessie en zonder sessie; sessiestatus zonder persoonsgegevens. |
| `browser_close` | Browser sluiten, lokaal profiel behouden. Dit logt niet uit. |

Voorbeeldzoekactie:

```json
{"query":"C#","location":"Utrecht","radiusKm":25,"pageSize":20}
```

Een zoekactie op alle bronnen bevat `searchId`, samengevoegde `jobs`, `sources` met afzonderlijke pagina's en `userActions`. `complete` betekent dat alle gevraagde bronpagina's beschikbaar zijn, niet dat alle vervolgpagina's zijn onderzocht. Ontbrekende login is een concrete actie, een bronfout blijft een fout. Hervatten bewaart oorspronkelijke parameters en eerder geslaagde bronresultaten maximaal 30 minuten in geheugen; na serverherstart is opnieuw zoeken nodig.

Vraag vervolgpagina's **per bron** met `source`, dezelfde zoekparameters en de bijbehorende `nextCursor`. Ook bij nul lokale treffers kan er een vervolg zijn. `sourceTotal` telt bronresultaten vóór lokale filtering; Vertage publiceert via deze route geen vastgesteld totaal en geeft `null`. Een volle Vertage-pagina heeft een vervolg; een korte/lege pagina beëindigt die reeks. Onbekende filters worden afgewezen. Categorie-ID's zijn uitsluitend voor expliciete bronzoekacties. Bron- en lokale voorwaarden staan afzonderlijk in de response.

Paginagrootte 1–100; maximaal offset 999, HTTP-timeout 20 seconden, detailactie maximaal 60 seconden en 1000 terugzoekrecords. Bronresponse maximaal 10 MiB, toolresponse 2 MiB. Een overschrijding is een expliciete fout, nooit stil afgekapt detail. Offsetpaginering is geen snapshot: bronwijzigingen kunnen overlap of omissies geven. Dedupliceer vervolgpagina's bij de host op `(source, sourceId)`.

## Model en bronadapter

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

Profiel: `%LOCALAPPDATA%/ZZPMcp/striive-profile` (Edge) of `striive-chrome-profile` (Chrome), buiten de repository. Gebruik één serverproces tegelijk per profiel. Sluiten behoudt het profiel; uitloggen gebeurt in de site. Bij browser-/netwerkfouten wordt de sessie niet als bruikbaar gerapporteerd. Automatische MFA en login zonder browser zijn niet gebouwd. De interne API-route is geen officiële API-garantie. De adapter gebruikt `useMatchingScore=false`; bronlocatiefilter en `maxRadius` worden doorgegeven, maar exacte afstandssemantiek is niet vastgesteld. C# en .NET geven verschillende bronresultaten: de AI-host onderzoekt synoniemen. Signed afbeeldingslinks en persoonlijke account-/matchingvelden worden uit details en fixtures verwijderd.

Zie [zoeken op alle bronnen en live bewijs](docs/multi-source-search.md), [API-bevindingen](docs/harvey-nash-api.md), [AI-hostworkflow](docs/host-workflow.md), [verificatieverslag](docs/verification.md) en [plan met precies vijf reviews](AI/plans/zzp-mcp.md).

Een persoonlijk voorbeeld met het actuele zoekprofiel en cv van Jos staat in [examples/jobsearch](examples/jobsearch/README.md). Verwijs naar die bestanden bij een nieuwe zoekvraag; de MCP-server ontvangt alleen de daarvan afgeleide zoekparameters.
