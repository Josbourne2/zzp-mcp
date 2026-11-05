# ZZP MCP — uitvoeringsplan

Status: oorspronkelijke PoC geïmplementeerd na precies vijf reviewrondes, revisie 5. Daarna op expliciet gebruikersverzoek uitgebreid: `search_jobs` doorzoekt standaard Harvey Nash én Striive/Vertage, opent zo nodig een loginvenster en geeft een concrete loginactie terug. `resume_search` hervat dezelfde vraag met behoud van geslaagde bronresultaten. Beide adapters, volledige Vertage-details en login/hervatten zijn live geverifieerd; 59 offline tests slagen. Zie [actuele uitbreiding en bewijs](../../docs/multi-source-search.md). De oorspronkelijke afbakening hieronder en de vijf reviewrondes blijven als historie bewaard; de uitbreiding vervangt de eerdere keuze om Vertage uitsluitend als browserproef op te leveren.

## Uitgangspunt en afbakening

Repository geïnspecteerd op 5 november 2026: alleen README, .gitignore en lokale Serena-configuratie; geen bestaande toepassing, tests of eerder plan gevonden. Er staat geen AGENTS.md in de repository of bovenliggende onderzochte mappen. De door de gebruiker meegegeven instructie geldt: Nederlands communiceren.

Eerste oplevering: lokale C#/.NET MCP-server via stdio, één volledig werkende Harvey Nash-adapter, een kleine afzonderlijke browserloginproef en een aantoonbare zoek/detailflow met een geschikte lokale MCP-client. De AI-host doet cv-analyse, synoniemen, matching en presentatie. Geen cv-opslag, eigen model, website, accountsysteem, scheduler, sollicitatieflow of lege toekomstige adapters.

Harvey Nash en browserlogin zijn onafhankelijke bewijsstukken. Geen bewezen browsertoegang betekent een expliciet onvolledig bewijs, niet automatisch stoppen met de overige onderdelen. De andere genoemde bronnen blijven vervolgwerk.

## 1. Technische keuzes en API-onderzoek

1. Controleer geïnstalleerde .NET SDK en officiële documentatie voor de gekozen ondersteunde .NET-, MCP SDK- en HTML-parserversies; leg exacte versies vast. Houd transport, toepassing en bronadapter gescheiden, zonder vooraf uitgebreide lagen te bouwen.
2. Onderzoek het aangeleverde POST-endpoint `https://www.harveynash.nl/_sf/api/v1/jobs/search.json` met kleine reproduceerbare requests. De aangeleverde payload en responsebeschrijving zijn hypothesen, geen reeds bewezen contract.
3. Verifieer lege en niet-lege query, zoekbetekenis, locatie/radius en miles versus kilometers, filter-ID's uit histogrammen, paginagrootte, offset/token en beëindiging. Vergelijk pagina-ID's om overlap en onvolledigheid zichtbaar te maken. Claim geen volledig bronbereik wanneer dit niet aantoonbaar is.
4. Verifieer directe vacaturelinks en een detailstrategie voor bron-ID, ook na cacheverloop of serverherstart. Verzin geen detailendpoint. Onderzoek of dezelfde API een ID kan terugvinden; anders documenteer de begrensde terugzoekstrategie en de fout wanneer terugvinden niet lukt.
5. Bewaar beknopte API-bevindingen, reproduceerbare requests en een geschoonde fixture met behoud van representatieve categorieën, HTML, bedragen, nulwaarden, paginering en onbekende velden.

Reeds door de uitvoerende agent live gecontroleerd: endpoint zonder authenticatie werkt; de test gaf 42 treffers; offset 3 met paginagrootte 3 geeft andere ID's; `C#` gaf één treffer; Utrecht met radius 5 miles gaf zes treffers en locatie-metadata; `filters: {categoryId: [valueId]}` voor Clients/Gasunie gaf zeven treffers. Dit zijn tijdgebonden waarnemingen, geen vaste verwachte aantallen. .NET 10 en MCP SDK 2.3.1 zijn tegen officiële documentatie gecontroleerd. Gebruik de bewezen offsetroute; gebruik `next_page_token` pas na een aparte semantische verificatie.

Aanvullend live vastgesteld: zoeken op een bestaande bron-ID gaf exact die ID en totaal 1; een niet-bestaande sentinel gaf totaal 0. Detailstrategie: eerst zoeken op ID en exact de geretourneerde ID controleren, anders begrensd scannen. Een zoekhit op alleen vergelijkbare tekst is geen detailmatch. Alleen een volledig doorzochte bron mag `not_found` opleveren. Een scanlimiet of herhaalde bronpagina geeft `search_incomplete` met reden; onvoldoende onderzocht is geen bewezen afwezigheid.

Resultaat: gecontroleerd broncontract met expliciete onzekerheden. Niet-aangetoonde bronfilters worden niet als ondersteund gepubliceerd.

Valideer bij HTTP 200 minimaal het responsecontract: `results` is een array, elke job heeft een geldig bron-ID en paginagegevens zijn onderling consistent. Ontbrekende of verkeerd getypeerde verplichte velden, HTML of een foutobject zijn een broncontractfout, nooit een succesvolle lege lijst. Onbekende extra velden blijven toegestaan.

## 2. Vroege browserloginproef

Voer deze kleine proef direct na het eerste API-onderzoek uit, voordat een complete tweede adapter wordt overwogen. Gebruik Striive als representatieve bron: de gebruiker heeft bevestigd een account te hebben en zelf te kunnen inloggen. Probeer de browserroute daarom daadwerkelijk; ontbrekende menselijke login is pas na die poging een mogelijke blokkade. Gebruik geen veronderstelde officiële API.

- Open lokaal een zichtbare browser met een eigen ZZP MCP-profiel; de gebruiker logt zelf in inclusief MFA.
- Start verbinden en controleer status met afzonderlijke korte acties. Een menselijke bevestiging is geen sessiebewijs: controleer een bronspecifiek beschermd signaal én een geslaagde leesactie.
- De minimale sessiegrens heeft starten en korte brongevalideerde statuscontrole; alleen die validatie mag `verbonden` opleveren. Een 401, 403, loginredirect of login-HTML bij een beschermde leesactie invalideert de status. Een eerdere geslaagde login is geen permanent sessiebewijs.
- Toon minimaal de toestanden niet verbonden, wacht op login, verbonden, login verlopen en fout. Een loginpagina is een authenticatiefout, nooit een lege zoekuitkomst.
- Toon dat een zoekactie bij sessieverloop een herstelbare fout geeft en na herlogin met dezelfde zoekparameters kan worden herhaald. Automatisch hervatten is niet nodig voor deze proef.
- Bewaar profiel en sessiegeheimen uitsluitend lokaal, buiten git, modeluitvoer en logs. Vraag geen wachtwoord via chat.

Resultaat: kort bewijsverslag met uitgevoerde handelingen en resultaten. Ontbrekende toegang of menselijke login wordt als concrete externe blokkade genoteerd, met exact het onbewezen deel. Een browser die alleen opent is onvoldoende bewijs.

## 3. Minimaal toepassingscontract en MCP-tools

Gebruik een `IJobSource` met broninformatie/capabilities, zoeken en details ophalen. Maak een kleine afzonderlijke sessiegrens alleen waar de browserproef die nodig heeft. MCP-tools kennen geen concrete Harvey Nash-implementatie.

| Mogelijkheid | Minimaal contract |
| --- | --- |
| Bronnen/status | Bron-ID, implementatiestatus, verbindingsstatus, werkelijk ondersteunde filters en pagina-opties. Geplande bronnen verschijnen niet als werkende adapter. |
| Zoeken | Bronselectie, vrije zoekterm, expliciete filters, begrensde paginering; compacte treffers met bron-ID, link en detailverwijzing. |
| Details | Bron en bron-ID; volledige leesbare tekst, genormaliseerde gegevens, bewijs en oorspronkelijke ophaaltijd. Cachemisser heeft een expliciete terugzoekroute of fout. |
| Browserproef | Kort starten van verbinden en apart status controleren; geen langdurige MCP-call die op menselijke login wacht. |

Elke zoekresponse vermeldt toegepaste bronfilters, lokale filters, niet-toegepaste voorwaarden, bron/totaal voor zover bekend, onderzochte aantallen, vervolgmogelijkheid en eventuele onvolledigheid. Een onbekende of niet-ondersteunde filter geeft een validatiefout. Bij onzekere bronsemantiek wordt het filter als niet toegepast gemeld, met onvolledigheid en zonder matchingclaim; nooit stilzwijgend negeren. Lokale filters mogen niet de indruk wekken dat een bronbreed totaal het aantal passende vacatures is. Geef `sourceTotal` apart van een nullable lokaal totaal. Bereken de vervolg-offset met het aantal geconsumeerde bronrecords, ook wanneer lokale filtering nul treffers oplevert.

Kies eenvoudige begrensde paginering op basis van bewezen API-gedrag. Bind vervolginformatie aan de oorspronkelijke zoekparameters. Publiceer geldige categorie- en waarde-ID's plus labels via facetten in de zoekresponse of broninformatie. Geef de bronwaarneming en zoekscope van de facetkeuzes aan; een beperkte zoekresponse is geen volledige broncatalogus. Stel een maximum voor pagina's, resultaten, responsetekst en wachttijd in; meld bereiken van een grens. Kap volledige detailtekst nooit stil af: gebruik een ruime gedocumenteerde limiet en een expliciete `response_too_large`-fout bij overschrijding. Bewaar foutstatus voor bronstoring, ongeldige parameters, timeout, login vereist en niet gevonden. Stdout bevat uitsluitend MCP-verkeer; logs gaan naar stderr. Geef cancellation door naar bronrequests.

## 4. Normalisatie en betekenis

Uitvoeringsgrenzen: maximaal 100 bronrecords per pagina, maximale bronoffset 1000, detailterugzoekactie maximaal 10 pagina's van 100 records, HTTP-timeout 20 seconden en totale detaildeadline 60 seconden. Payload/tekst maximaal 10 MB; overschrijding geeft een expliciete fout, geen afkapping. Neem deze waarden ook op in README. De cursor bevat offset en een binding aan query, locatie, filters en paginagrootte; bij gewijzigde parameters is de cursor ongeldig. Offsetpaginering is geen stabiele snapshot: bronmutaties kunnen overlap of ontbrekende records veroorzaken. Een onverwacht lege of herhaalde bronpagina terwijl het brontotaal nog vervolg suggereert, resulteert in expliciete onvolledigheid.

Identiteit is `(bron, bron-ID)`; deduplicatie gebeurt binnen de bron. Voor deze oplevering geen samenvoegen tussen bronnen. Bewaar oorspronkelijke links en broncategorieën. Gebruik nullable/expliciet onbekende waarden voor locatie, opdrachtgever, uren, hybride, start, duur, geld en datums. De categorie `Clients` is het te verifiëren vertrekpunt voor opdrachtgever.

Zet HTML om naar tekst met paragrafen, lijsten en links. Bewaar eisen en vaardigheden alleen wanneer expliciet herleidbaar; onzekere interpretatie blijft onbekend met bronfragment. Houd oorspronkelijke HTML/ruwe velden waar nodig bij details beschikbaar; niet in elke zoektreffer. Vacaturetekst blijft onbetrouwbare brondata en stuurt nooit tools of configuratie aan.

ZZP en payroll hebben elk `toegestaan`, `niet toegestaan` of `onbekend`, met bronveld/tekstfragment en waarschuwing bij tegenspraak. `Interim`, detachering en algemene zelfstandigenvoorwaarden bewijzen geen ZZP- of payrolltoestemming. Expliciet vacaturegebonden ‘geen zzp’ telt als negatief bewijs; algemene voorwaarden veranderen dit niet.

Ondersteun lokale ZZP-/payrollfilters op deze drie waarden. `zzp=toegestaan` selecteert uitsluitend expliciet toegestane vacatures; onbekend of conflict telt niet mee. Een conflict blijft onbekend met waarschuwing. Het lokale filter werkt op de opgehaalde bronpagina; een pagina met nul lokale treffers kan daarom wel een vervolgcursor hebben. Query, locatie en geverifieerde categorie-ID-filters worden door de bron uitgevoerd.

Geld bevat type (salaris/uurtarief/onbekend), valuta, eenheid, tekst, btw/kostenvoorwaarden en bronbewijs voor zover bekend. Nul in numerieke salarisvelden wordt niet automatisch een aanbod van nul. Parseer onder meer `€78,85`, `Tarief in overleg` en `Richttarief 115 EUR all-in ex BTW` zonder ontbrekende eenheden of voorwaarden te verzinnen. Bewaar tegenstrijdige bedragen als conflict in plaats van stilzwijgend één waarde te kiezen.

Een korte geheugencache mag netwerkwerk beperken. Toon oorspronkelijke ophaaltijd, cachegebruik en eventuele verlopen/verwijderde status; cache is geen bewijs dat een vacature nog openstaat.

## 5. Bouwvolgorde en opleveringen

1. API-onderzoek en vroege browserproef afronden of exact begrenzen.
2. Minimaal model, broninterface en Harvey Nash HTTP-adapter bouwen; normalisatie en detailstrategie aansluiten.
3. MCP-tools via stdio toevoegen met inputvalidatie, grenzen, foutafhandeling en logging.
4. Betekenisvolle fixturetests, foutscenario's en echte lokale MCP-clientflow uitvoeren.
5. README aanvullen met installatie, startcommando, geteste clientconfiguratie, beperkingen en reproductiestappen. Leg API-bevindingen en browserbewijs apart vast.
6. Een AI-hostvoorbeeld documenteren: cv en actuele zoekvraag blijven bij de host; harde voorwaarden, voorkeuren en onbekenden scheiden; meerdere zoekrondes; volledige tekst controleren; maximaal circa twintig passende resultaten, zonder opvullen. Tabel bevat functie/opdrachtgever/link, aanbod, aansluiting, ZZP/payroll, voordelen en nadelen/open vragen; noemt onderzochte bronnen en zoekbeperkingen. Vervolgvragen kunnen een nieuwe zoekronde starten.

## 6. Verificatie en gereedcriteria

| Onderdeel | Vereist bewijs |
| --- | --- |
| Build en start | Schone checkout kan herstellen, bouwen en de gedocumenteerde stdio-server starten met de vastgelegde SDK/packageversies. |
| MCP-bruikbaarheid | Geschikte lokale client kan initialiseren, tools ontdekken, bronnen opvragen, zoeken en volledige details lezen. Test vermeldt client/versie en exact startcommando; geen onbewezen ChatGPT-claim. |
| API en pagina's | Live zoekrequest en detailophaalactie werken; reproduceerbare requests tonen query/filter/locatiegedrag en doorlopen van meerdere pagina's of een expliciet kleine testpagina. Grenzen en ontbrekende dekking zijn zichtbaar. |
| Detaillevensduur | Details van een actieve live-ID moeten na cacheverloop/herstart werkelijk opvraagbaar zijn via de bestaande zoek-API en begrensde scan. Alleen bij verwijdering, onbekende ID of overschrijding van de scanlimiet volgt een expliciete fout; uitsluitend cachegebaseerde details voldoen niet. Nooit een verzonnen detailendpoint of leeg succesvol detail. |
| Normalisatie | Fixturetests dekken HTML-structuur, opdrachtgever, ontbrekende data, stabiele identiteit, dubbele ID's, ZZP-negatie, interim, detachering versus payroll, algemene voorwaarden, conflicten en de drie tariefvoorbeelden. |
| Fouten en privacy | Timeout, cancellation, bronfout, ongeldige filter en sessieverloop geven herkenbare fouten; stdout blijft protocolzuiver. Geen cv, cookies of tokens in bronrequests, fixtures of logs. |
| Browserroute | Beschermd signaal, leesactie, detectie van verlopen sessie en herlogin/herhalen aangetoond, of precies als externe blokkade vermeld. |
| Hostworkflow | Voorbeeld met uiteenlopende zoekvragen toont dat het Utrecht/C#-voorbeeld geen ingebouwd profiel is en twintig alleen de shortlist begrenst. |

Rapporteer afzonderlijk: geslaagde offline checks, geslaagde live checks, niet-uitgevoerde checks met reden en bekende beperkingen. Live aantallen en velden kunnen wijzigen; fixturetests dienen regressie, niet als bewijs van actuele beschikbaarheid. Het Harvey Nash-deel is bruikbaar wanneer de lokale MCP-zoek/detailflow werkt en beperkingen zichtbaar zijn; de volledige browserroute heet pas bewezen na de beschreven praktijkproef.

Gecontroleerde offline tests dekken sessiestatusinvalidatie op 401/403, loginredirect en login-HTML, en bewijzen dat uitsluitend bronvalidatie de status verbonden kan zetten. Test daarnaast het onderscheid tussen een volledig doorzochte afwezige detail-ID (`not_found`) en een afgebroken/herhaalde scan (`search_incomplete`). Deze tests blijven duidelijk gescheiden van live browserbewijs.

Test lokale ternaryfiltering met onbekend/conflict en een nul-resultaatpagina waarvan de vervolgcursor een latere echte match bereikt. Test ook een voortijdig lege of herhaalde bronpagina bij een brontotaal dat vervolg suggereert: geen succesvolle complete zoekactie rapporteren.

Voeg een offline malformed-200/schemawijzigingstest toe voor ontbrekende/verkeerd getypeerde contractvelden, foutobject en HTML. Controleer ook dat onbekende extra velden worden geaccepteerd.

## Reviewlog

Precies vijf opeenvolgende reviews door een afzonderlijke devil’s-advocate-agent zijn afgerond. Elke ronde beoordeelde de actuele revisie; alle concrete kritiek is verwerkt. Er volgt geen zesde ronde. De planningsagent heeft de onderstaande wijzigingen vastgelegd.

### Ronde 1 — revisie 1

Overgenomen: strikte validatie van onbekende/niet-ondersteunde filters; onzekere bronsemantiek expliciet als niet toegepast. Bron- en lokale totalen gescheiden en vervolg-offset gebaseerd op geconsumeerde bronrecords, ook bij nul lokale treffers. Cold-cache/herstartdetails voor een actieve live-ID moeten echt werken; fouten alleen bij concrete terugzoekbeperkingen. Geen kritiek afgewezen. Daarnaast bevestigd Striive-account verwerkt: de browserroute wordt daadwerkelijk geprobeerd.

### Ronde 2 — revisie 2

Overgenomen: facet-ID's en labels vindbaar maken met expliciete bron-/zoekscope; volledige detailtekst nooit stil afkappen en overschrijding expliciet afwijzen. Live bevestigde exacte ID-query wordt de eerste detailroute met begrensde scanfallback. Geen kritiek afgewezen. Waargenomen loginredirect van `login.striive.com` naar `login.vertage.com` is uitsluitend een ingang voor de praktijkproef, geen sessiebewijs.

### Ronde 3 — revisie 3

Overgenomen: brongevalideerde korte sessiestatus en invalidatie bij 401/403, loginredirect of login-HTML, met afzonderlijke offline tests. Detailafwezigheid (`not_found`) onderscheiden van een onvolledige scan (`search_incomplete`), eveneens getest. Geen kritiek afgewezen; geen nieuwe dienst nodig.

### Ronde 4 — revisie 4

Overgenomen: precieze ternaryfiltersemantiek en lokale paginascope, incl nul lokale treffers met vervolg en tests voor onbekend/conflict. Concrete grenzen en cursorbinding uit uitvoeringsvoorstel vastgelegd. Voortijdig lege/herhaalde bronpagina en mutaties tijdens offsetpaginering leveren zichtbare beperkingen op. Geen kritiek afgewezen; geen architectuurwijziging nodig.

### Ronde 5 — revisie 5

Overgenomen: minimaal HTTP-200 responsecontract valideren; ontbrekende/verkeerde verplichte velden, HTML of foutobject geven broncontractfouten. Extra onbekende velden blijven toegestaan. Offline schemawijzigingstest toegevoegd. Geen kritiek afgewezen. De devil’s advocate ziet geen resterende architectuurblokkade; het plan is uitvoeringsgereed na precies vijf rondes.




