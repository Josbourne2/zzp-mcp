# ZZP MCP — plan voor SQLite en aanvullende bronnen

Datum: 5 november 2026. Status: **opslag, bestaande bronnen en duurzame onderzoekssessies opgeleverd**.

Laatste correctie op 6 november 2026: zoek- en verzamelscans verwijderen op expliciet gebruikersverzoek nooit vacatures, details, versies of eerdere sessieverwijzingen uit de gedeelde database. Elke collectie bewaart uitsluitend haar eigen waargenomen leden; .NET- en securityonderzoek behouden elkaars gegevens. Compleet geldt binnen de gebruikte bronvoorwaarden. Expliciete opschoning en bewaartermijnen blijven vervolgwerk. De eerdere verwijderafspraak hieronder is hiermee vervallen.

Vervolg op 6 november 2026: op gebruikersverzoek Vertage-bulkverzameling met de opgegeven size=1000-query en directe detailroute toegevoegd. SQLite-schemaversie 3 bewaart herstartbare collecties. Alleen ontbrekende details worden opgehaald; volledige bestaande details worden hergebruikt. Afwezige bron-ID's worden na een complete geslaagde lijstscan verwijderd binnen de expliciete bron-/portaalscope, inclusief hun vacaturegebonden historie. Dit vervangt voor die synchronisatie de eerdere afspraak om niets automatisch te verwijderen. De oorspronkelijke tekst hieronder blijft de planningshistorie; zie [actueel gedrag](../../docs/sqlite-storage.md#vertage-volledig-verzamelen-en-synchroniseren).

De gebruiker heeft na het schrijven van dit plan expliciet opdracht gegeven de implementatie te beginnen. De eerste stap bevatte SQLite-schemaversie 1, vacature-upserts, volledige detailversies en zoekpogingen/pagina's/treffers voor de bestaande adapters. Een vervolgstap voegt schemaversie 2 en onderzoekssessies toe: start_session, list_sessions, get_session en mark_job_reviewed. Sessies koppelen zoekacties en verzamelde vacatures, tonen openstaande pagina's en bewaren verwerking per exacte tekstversie, zonder historie te wissen. Dit maakt de bestaande RAM-resume niet duurzaam. Zie [implementatie en beheer](../../docs/sqlite-storage.md). Cache, FTS, volledige-tekstbatches, uitgebreid herkomstbewijs en nieuwe adapters zijn nog vervolgwerk. De onderstaande oorspronkelijke onderzoeksafspraken blijven gelden voor nieuwe bronnen.

## Opdracht en grens

Dit document beschrijft de vervolgstappen voor lokale opslag, tekstzoeken en nieuwe bronadapters. Het schrijven van dit plan geeft geen toestemming voor implementatie. De gebruiker wil iedere site eerst zelf handmatig bekijken en samen onderzoeken hoe de site zijn backend-API gebruikt. Daarna volgt een concreet adaptervoorstel; bouwen gebeurt uitsluitend na een expliciete vervolgopdracht.

Tijdens het schrijven van dit plan zijn geen code, configuratie of packages gewijzigd. De eerder toegevoegde packageverwijzing naar `Microsoft.Data.Sqlite` 10.0.14 en bijbehorende herstelbestanden zijn bij de implementatie gebruikt. Het oorspronkelijke [PoC-plan](zzp-mcp.md) blijft als historie staan.

## Productdoel: breed verzamelen, inhoudelijk onderzoeken door de agent

De MCP-server is primair een verzamelaar en ontsluiter van vacatureteksten: een veredelde webscraper met bronadapters en lokale opslag. Het doel is een zo breed mogelijke verzameling binnen relevante rolgebieden op één plek, zodat de agent alle volledige teksten kan onderzoeken. Alleen vacatures met een letterlijk trefwoord zoals C# of .NET vinden is onvoldoende. Verzamel bijvoorbeeld ontwikkelaars- en architectuurrollen onafhankelijk van hun stack, en beoordeel hun werkelijke aansluiting daarna.

De server verzorgt verzamelen, paginering, volledige details, normalisatie, bronherkomst en duurzame opslag. De agent doet inhoudelijke screening, eventuele samenvatting, cv-matching en selectie. Alleen expliciet gevraagde harde grenzen mogen de verzameling beperken; voorkeursfilters en bronrangschikking mogen niet ongemerkt de brede verzameling vervangen.

De meegeleverde [skill zzp-jobsearch](../../skills/zzp-jobsearch/SKILL.md) legt de agentwerkwijze vast. Het bestand is onderdeel van deze repository; automatische installatie of laden via het MCP-protocol is hiermee niet gerealiseerd. Neem de skill bij toekomstige distributie op naast de server, met hostspecifieke installatie-instructies. Voeg geen cv of persoonlijk zoekprofiel toe aan de generieke skill.

## Situatie bij het opstellen van het plan

- Harvey Nash en Vertage/Striive hebben een geïmplementeerde `IJobSource`. `search_jobs` zoekt standaard op beide bronnen met één generiek zoekobject; bronspecifieke filters blijven bronspecifiek.
- Een bron die login nodig heeft, geeft een herstelbare status. Andere bronnen kunnen alvast resultaten leveren. Na menselijke login hervat `resume_search` dezelfde zoekvraag en behoudt geslaagde bronresultaten.
- Hervatten gebruikt nu uitsluitend procesgeheugen: maximaal 32 zoekacties, 30 minuten geldig. Na een serverherstart zijn die zoek-ID's ongeldig. Verlopen acties worden bij een nieuwe zoekactie opgeruimd, niet door een achtergrondtimer.
- Er is geen duurzame vacatureopslag of detailcache. `get_job` haalt de bron opnieuw op. De SQLite-packageverwijzing is aanwezig, maar opslagcode en schema ontbreken.
- Ontdubbelen gebruikt momenteel bron plus bron-ID binnen een response. Er is geen centrale index voor dezelfde vacature uit verschillende zoekrondes.
- Het browserprofiel bewaart sessiegegevens lokaal. Dit profiel is geen vacaturecache.

Referenties: [zoekgedrag en live bewijs](../../docs/multi-source-search.md), [modellen](../../src/ZzpMcp/Models.cs), [zoekservice](../../src/ZzpMcp/JobService.cs), [zoekprofiel](../../examples/jobsearch/zoekprofiel-jos-menhart.md).

## 1. Verplichte handmatige inspectie per bron

Deze stap geldt voor iedere nieuwe site en ieder afzonderlijk klantportaal. Een vergelijkbare naam of vormgeving bewijst geen gedeelde backend.

1. De gebruiker opent de site, bekijkt het aanbod en logt waar nodig zelf in, inclusief MFA. Er worden geen accounts aangemaakt of abonnementen afgesloten zonder afzonderlijke opdracht.
2. Samen bekijken we het netwerkverkeer bij een lege zoekvraag en bij representatieve termen: `software developer`, `lead developer`, `software architect`, `.NET`, `C#`, `SQL Server` en `DBA`.
3. Leg de werkelijk uitgevoerde leesrequests vast: methode, URL, veilige headers, parameters, body en responsevorm. Geen endpoint afleiden uit een vermoedelijke naam. Noteer of de route openbaar, accountgebonden of portalgebonden is.
4. Verifieer de betekenis van filters: locatie/radius en eenheden, categorieën, uren, contractvorm en sortering. Maak onderscheid tussen serverfilter, browserfilter en onbewezen filter.
5. Onderzoek paginering: page/offset/cursor, paginagrootte, totaal indien beschikbaar, einde van de lijst, overlap, herhaalde pagina's en effect van wijzigingen tijdens het bladeren.
6. Open opdrachtgegevens en controleer stabiele bron-ID, detailroute, volledige tekst, opdrachtgever, oorspronkelijke aanbieder, links, publicatie/wijzigingsdatums en sluitingsstatus. Een lijstfragment is geen volledige tekst.
7. Bij beschermde bronnen: controleer technisch login, sessieverloop, uitloggen en herlogin. Een loginpagina of foutobject mag nooit een succesvolle lege lijst worden. Test dat een identieke leesactie na herlogin weer werkt.
8. Leg waargenomen limieten en foutgedrag vast. Respecteer `Retry-After` en gebruik begrensde requests; geen belastingproeven.
9. Maak een beknopt onderzoeksverslag met geschoonde requestvoorbeelden en fixtures. Geen cookies, tokens, signed URL's, accountvelden of ongeschoonde HAR-bestanden in git, logs of chat. Vacaturetekst blijft brondata en geldt nooit als instructie.
10. Presenteer daarna het adaptervoorstel: bewezen mogelijkheden, ontbrekende mogelijkheden, voorgestelde mapping, tests en open vragen. Wacht op de expliciete implementatieopdracht.

**Gereed wanneer:** de gebruiker de site heeft bekeken, een zoek- en detailflow reproduceerbaar is beschreven, de toegang bekend is en de onzekerheden zichtbaar zijn. Een openbare webpagina alleen bewijst geen bruikbare zoek-API.

## 2. Bronnen en onderzoeksvolgorde

De onderstaande pagina-observaties komen uit de eerdere inventarisatie op 5 november 2026. Ze zijn geen afgeronde handmatige backend-inspecties. Alle nieuwe adapters staan op **niet geïmplementeerd** en verschijnen niet als beschikbare bron in `list_sources`.

| Fase | Bron en ingang | Bekend vertrekpunt | Eerst te onderzoeken |
| --- | --- | --- | --- |
| A | Harvey Nash en Vertage/Striive | Bestaande adapters en eerder technisch gecontroleerde leesflows | Met de gebruiker de bestaande routes als referentie bekijken; huidige identiteit, sessiegedrag, paginering en ontbrekende herkomstvelden vastleggen. Geen herbouw zonder reden. |
| B1 | [Circle8](https://www.circle8.nl/zelfstandig-professionals), [politieportaal](https://np.circle8.nl/) | Bronfamilie met afzonderlijke klantportalen; politieportaal gevonden | Centrale lijst versus klantportalen, stabiele portal-ID's en sessiegrenzen. UWV en andere portalen pas opnemen na afzonderlijke verificatie. Een generieke portaladapter pas na bewijs van hetzelfde contract. |
| B2 | [Flextender](https://www.flextender.nl/opdrachten/) | Open opdrachtenpagina en afzonderlijke app/login zichtbaar | Lijst- en detailrequests, zoekparameters, paginering, opdrachtgever en expliciete ZZP/detacheringsvoorwaarden. |
| B3 | [Magnit Source](https://magnit-source.magnitglobal.com/browse) | Browse-ingang gevonden; eerdere tekstextractie leverde geen leesbare inhoud | Browserrequests en toegang inspecteren. Nederlandse dekking en mogelijke klantportalen, waaronder TenneT, afzonderlijk vaststellen; geen API of beschikbaarheid veronderstellen. |
| B4 | [Freelance.nl](https://www.freelance.nl/interim-opdrachten) | Open opdrachtenoverzicht en accountomgeving zichtbaar | Welke tekst en aanbieder openbaar zijn, eventuele accountgrenzen, details en herkomstlinks. Aggregatorvermeldingen behouden. |
| C1 | [Computer Futures](https://www.computerfutures.com/en-nl/find-a-job/) | Technologiegerichte zoekingang aanwezig | Nederlandse contractopdrachten onderscheiden van vaste banen; filters, bron-ID, detailtekst en aanvullend relevant aanbod. |
| C2 | [Freep](https://www.freep.nl/) | Open overzicht met ICT-, provincie- en contractvormkeuzes | Werkelijke filterrequests, volledige detailtoegang en bewijs voor freelance versus detachering. |
| C3 | [Opdracht Overheid](https://www.opdrachtoverheid.nl/) | Filters voor geschiktheid, ICT, locatie, uren en tarief zichtbaar | Backendbetekenis van filters en onderscheid tussen ZZP en verschillende loondienstvormen. |
| C4 | [MiPublic ICT/Data](https://mipublic.nl/vakgebied/ict-data) | Overzicht met werkvorm, locatie en tarieven zichtbaar | Zoek/detailroutes, oorspronkelijke aanbieder, contractbewijs en overlap met andere bronnen. |
| C5 | [Freelancer.nl](https://freelancer.nl/opdrachten) | Eigen opdrachtenlijst en filters | Zelfstandig onderzoeken; dit is een andere site dan Freelance.nl. Detailtoegang, identiteit en relevantie van projectaanbod vaststellen. |
| D1 | [Freelapp](https://freelapp.nl/sectoren/it) | IT-overzicht; presenteert zich als verzamelplatform | Externe aanbieders, oorspronkelijke links en herkomstketen per opdracht. Vergelijk aanvullend bereik met directe bronnen. |
| D2 | [xFreelance](https://www.xfreelance.nl/opdrachten) | Overzicht toont onder andere Flextender als bron | Herkomstvelden en doorkliklinks vastleggen; dezelfde publicatie herkennen zonder de aggregatorvermelding te verliezen. |
| E | [Jellow FAQ](https://www.jellow.nl/faq) | Volgens FAQ zijn opdrachten besloten en alleen zichtbaar na uitnodiging | Met gebruikersaccount bekijken wat werkelijk leesbaar is. Mogelijk alleen persoonlijke uitnodigingen ondersteunen; geen algemene vacaturezoeker beloven. |
| F | LinkedIn | Aanvullende onderzoeksbron, geen bewezen zoekadapter | Bruikbaarheid, toegang en dekking van Job Library afzonderlijk onderzoeken; gesloten vacatures onderscheiden van actuele opdrachten. |

LinkedIn: de officiële [Job Posting API](https://learn.microsoft.com/en-us/linkedin/talent/job-postings/api/sync-job-postings?view=li-lts-2026-04) is bedoeld voor plaatsen en beheren, niet als bewijs van een algemene zoek-API. De [Job Library](https://www.linkedin.com/help/linkedin/answer/a7452305) beschrijft betaalde vacatures die na 1 april 2025 verschenen; vermeldingen blijven tot een jaar na sluiten staan. Onderzoek toegang tot de bijbehorende API, toepasselijke dekking en datums voordat een koppeling wordt voorgesteld. Geen claim dat hiermee alle LinkedIn-vacatures beschikbaar zijn.

Na B4 prioriteren we C en D op het aantal aanvullende relevante opdrachten, de kwaliteit van herkomstinformatie en de kosten van toegang en onderhoud. Meet dit met overlappende zoekvragen en vergelijkbare tijdvakken; noem een lager zoekresultaataantal niet automatisch slechtere dekking.

## 3. SQLite als relationele bron van waarheid

Ontwerp en implementeer dit pas na een expliciete vervolgopdracht. Nieuwe bronadapters blijven daarnaast afhankelijk van hun eigen handmatige inspectie. De opslag kan daarna eerst met de bestaande twee adapters worden gebouwd.

Gebruik `Microsoft.Data.Sqlite` met parameterqueries, foreign keys, transacties en een eenvoudig versieerbaar schema. Geen aparte database- of zoekservice. De database staat buiten de repository, voorgesteld onder `%LOCALAPPDATA%\ZZPMcp\jobs.db`, met een configureerbaar pad voor tests en lokale omgevingen.

Voorgesteld model:

| Entiteit | Betekenis en relaties |
| --- | --- |
| Bron/portaal | Adapterfamilie plus stabiel portaal/tenant waar nodig. Gebruik één vaste niet-null standaardwaarde voor bronnen zonder portaal om unieke sleutels eenduidig te houden. |
| Vacature | Interne sleutel en unieke combinatie `(bron, portaal, bron-ID)`. Huidige samenvatting, eerste/laatste waarneming en afzonderlijke tijdstippen voor lijst en detail. Behoud bestaande bron-ID's en compatibele publieke identifiers. |
| Vacatureversie | Genormaliseerde volledige tekst en velden, inhoudshash, ophaaltijd en verwijzing naar vacature. De hash sluit vluchtige ophaaltijden en sessievelden uit. Een kort lijstrecord overschrijft nooit een volledig detailrecord. |
| Herkomstverwijzing | Vindplaats, publicatielink, eventuele oorspronkelijke aanbieder en oorspronkelijke URL met veld/tekstbewijs en waarnemingstijd. Opdrachtgever is een afzonderlijk gegeven en niet automatisch de aanbieder. Onbekend blijft onbekend; meerdere schakels blijven bewaard. |
| Zoekronde | Generieke zoekvraag, geselecteerde bronnen, starttijd en status. Bronrequests/filters afzonderlijk vastleggen zodat dezelfde vraag per bron controleerbaar blijft. |
| Zoekrondebron en pagina | Status, waarschuwingen, toegepaste filters, tellingen, vervolggegevens en ophaaltijd per bron/pagina. Geen sessiegeheimen bewaren. |
| Zoektreffer | Many-to-manyrelatie tussen zoekronde/pagina en vacature, met waarneming en volgorde. Eén vacature kan in meerdere rondes voorkomen zonder een nieuwe vacature te worden. |
| Mogelijke dubbel | Relatie tussen afzonderlijke vacatures met reden, bewijs, score indien gebruikt en status zoals kandidaat/bevestigd/afgewezen. Geen records wissen of bronidentiteiten vervangen. |
| Agentafleiding (optioneel vervolg) | Samenvatting of analyse door de agent, gekoppeld aan exacte vacatureversie, vraag/profiel indien relevant en afleidingsversie. Afgeleid resultaat blijft gescheiden van bronfeiten en vervangt nooit de volledige tekst. |

Beoordelingen op basis van het CV zijn vervolgwerk: indien toegevoegd, koppel ze aan vacatureversie en zoekprofielversie. Een inhoudelijke wijziging kan een nieuwe beoordeling nodig maken. Sla CV of beoordelingen niet stilzwijgend als cachemetadata op.

Houd genormaliseerde velden voor sleutels, relaties en filters beschikbaar. JSON kan brongebonden aanvullende gegevens bewaren, maar vervangt de relaties niet. Persoonlijke matchingvelden, cookies, authheaders en ondertekende assetlinks worden niet opgeslagen. Beslis vóór eventuele ruwe payloadopslag expliciet welke geschoonde velden nodig zijn.

## 4. Cachegedrag en lokale tekstzoekfunctie

- **Live zoeken:** `search_jobs` blijft de geselecteerde bronnen raadplegen voor nieuwe opdrachten. Zoekgeschiedenis vervangt geen actuele bronzoekactie. Cache en historie worden bijgewerkt met idempotente upserts.
- **Details:** introduceer een configureerbare versheidsduur en expliciete `forceRefresh`. Voorstel voor de eerste versie: 30 minuten voor details; definitief vastleggen bij implementatie. Een cachetreffer behoudt zijn oorspronkelijke ophaaltijd en meldt `cacheUsed`, versheid en eventuele historische status.
- **Autorisatie:** beschermd actueel ophalen behoudt de bestaande sessievalidatie. Een cache mag login_required of een bronfout niet maskeren als verse beschermde bronresponse. Als lokale historische leesactie zonder brontoegang wordt ondersteund, heet en gedraagt die zich expliciet zo; geen impliciete omleiding in `get_job`.
- **Afwezigheid:** niet terugkomen in één zoekronde bewijst geen sluiting. Bewaar expliciete bronstatus en vervaldatum afzonderlijk. Een historische vacature krijgt nooit vanzelf de status nog open.
- **Lokaal zoeken:** stel een aparte MCP-tool voor, bijvoorbeeld `search_cached_jobs`, met bron/portaal, begrensde paginering, opgeslagen tijdstippen en detailbeschikbaarheid. Meld duidelijk dat uitsluitend eerder opgehaalde inhoud wordt doorzocht en dat de dekking onvolledig kan zijn.
- **FTS5:** indexeer titel en beschikbare opdrachttekst, met hogere titelweging als startpunt. Gebruik één gedocumenteerde normalisatie voor index en zoekvraag, met expliciete tests voor `C#`/`csharp`, `.NET`/`dotnet` en `SQL Server`. Bewaar de originele tekst. Vermijd onbedoelde omzetting van C# naar alleen C of een fictieve semantische gelijkstelling van alle SQL-producten.
- **Indexconsistentie:** werk vacatureversie en index atomair bij, via transacties/triggers of één gecontroleerd schrijfpad. De zoekindex is volledig opnieuw op te bouwen uit de relationele opslag. Geen vrije FTS-syntax of SQL uit een model zonder validatie.
- **Hervatten:** bestaande kortdurende RAM-acties blijven aanvankelijk behouden. Duurzame zoekgeschiedenis betekent niet dat een oude `resume_search` na herstart geldig wordt. Persistente hervatting is afzonderlijk vervolgwerk met eigen versie-, cursor- en sessievalidatie.

Lucene.NET blijft een mogelijke latere vervanging van de zoekindex wanneer een vergelijking met echte vragen aantoonbaar betere resultaten geeft. SQLite blijft dan de relationele opslag; geen dubbele bron van waarheid.

### Volledige verzameling ontsluiten en analyseren

Ontwerp naast zoeken een manier om alle opgeslagen vacatures in een verzameling stabiel en begrensd op te sommen en hun volledige teksten batchgewijs te lezen. Dit is een toekomstig toolcontract, geen bestaande tool. Een FTS-trefferlijst mag niet de enige toegang tot het corpus zijn. Registreer welke bronpagina's zijn doorlopen, welke detailteksten ontbreken en welke tekstversies zijn geanalyseerd. Maak verzamelingen en leden reproduceerbaar, bijvoorbeeld met een vastgelegde ledenlijst en stabiele paginering, zodat gelijktijdige verversing niet ongemerkt tot overgeslagen teksten leidt.

Het ophalen van details geldt voor alle verzamelde vacatures in de afgesproken rolgebieden, niet uitsluitend voor eerder op trefwoorden geselecteerde kandidaten. Voer dit begrensd uit, met herstartbare voortgang en zichtbare fouten. Maak onderscheid tussen zoekdekking, detaildekking en analysedekking. Bestaande RAM-resume blijft een andere functie dan deze toekomstige duurzame verzamelvoortgang.

De agent kan eerst iedere volledige tekst verwerken tot een herleidbare samenvatting en daarna de samenvattingen doorzoeken. Bewaar originele tekst, versie, relevante bewijsfragmenten, onbekenden en verantwoordelijkheid/taken. Markeer afleidingen als mogelijk verouderd na een inhoudswijziging. Een ontbrekend gegeven in een samenvatting is geen bewezen afwezigheid in de vacature. Controleer de volledige tekst bij aanbevelingen, harde afwijzingen en nieuwe vragen die de samenvatting niet dekt. FTS en samenvattingen helpen navigeren; ze vervangen geen brede inhoudelijke screening. De server bevat hiervoor geen ingebouwd model of persoonlijke matchinglogica.

## 5. Ontdubbelen en herkomst tonen

Binnen dezelfde bron/portaal-identiteit: één vacature, meerdere zoektreffers en eventueel meerdere inhoudsversies. Dezelfde opdracht uit de rondes `.NET`, `software developer` en `architect` wordt hierdoor niet telkens opnieuw als nieuwe vacature opgeslagen.

Tussen bronnen: nooit uitsluitend op titel samenvoegen. Sterke signalen zijn een expliciet oorspronkelijk publicatie-ID of een gecontroleerde oorspronkelijke URL. Teksthash, opdrachtgever, locatie, datums en uren kunnen kandidaten opleveren, maar identieke standaardteksten bewijzen geen gelijke opdracht. Een repost kan bovendien een nieuwe publicatie of versie zijn.

Toon mogelijke dubbelen samen met de reden en alle vindplaats- en oorspronkelijke links. Bewaar tarieven, contractvormen en deadlines per publicatie: deze kunnen tussen aanbieders verschillen. Een herkomstketen van aggregator naar broker naar opdrachtgever wordt niet tot één naam teruggebracht.

## 6. Bouwfasen na afzonderlijke opdracht

1. **Schema en opslag:** migratieversie, unieke sleutels, foreign keys, transacties, configureerbaar bestandspad en testdatabase. Voeg repository-uitsluiting voor database-, WAL- en SHM-bestanden toe wanneer de implementatie wordt opgedragen.
2. **Bestaande bronnen aansluiten:** registreer live zoekrondes, bronstatussen en pagina's; voer vacature-upserts uit; bewaar volledig detail en inhoudsversies. Wijzig het externe generieke zoekcontract zo weinig mogelijk.
3. **Detailcache, verzameling en FTS5:** voeg zichtbare cachemetadata, versheidsbeleid, force-refresh en expliciet lokaal zoeken toe. Ontsluit ook alle verzamelde vacatures en volledige teksten in stabiele batches, inclusief detailachterstand en herstartbare verzamelvoortgang. Browserlogin en gedeeltelijke bronresultaten blijven werken. Samenvattingen worden door de agent gemaakt; eventuele opslag ervan is afzonderlijk vervolgwerk.
4. **Herkomst en mogelijke dubbelen:** breid modellen en presentatie uit met bewijsbare herkomst en relaties; toon nooit verzonnen aanbieders. Test tegen een dataset met bekende dubbelen en vergelijkbare maar verschillende opdrachten.
5. **Nieuwe adapters één voor één:** na iedere handmatige inspectie en expliciete bouwopdracht één adapter opleveren met geschoonde fixtures, negatieve gevallen en één echte zoek/detailproef. Daarna pas registreren als beschikbare bron.
6. **Onderhoud vastleggen:** versieschema, herbouw van FTS, backup/herstel en configureerbaar bewaarbeleid. Bepaal vóór automatische opschoning bewaartermijnen voor historie, details en versies; verwijder niet stilzwijgend handmatig bewaarde relaties of beoordelingen. Tot die beslissing alleen expliciete opschoning, geen verborgen verval van duurzame geschiedenis.

Bij databasefouten blijft duidelijk of de bron wel is gelezen en alleen opslag mislukte. Geen succesvolle opslag claimen na een rollback, geen halve versie/indexmutaties en geen database-error omzetten in nul treffers. Gebruik korte transacties, een begrensde busy-timeout en afhandeling voor gelijktijdige zoekacties. Een bestand van een nieuwer onbekend schema wordt niet stilzwijgend teruggezet.

## 7. Acceptatie en verificatie bij uitvoering

| Onderdeel | Vereist bewijs |
| --- | --- |
| Persistentie | Een tweede proces/opening ziet eerder opgeslagen vacatures en zoekrelaties; RAM-resume blijft expliciet afzonderlijk. |
| Identiteit en relaties | Herhaalde opslag en meerdere zoektermen maken één vacature met meerdere treffers. Gelijk bron-ID in andere bron of ander portaal blijft afzonderlijk. Foreign keys en unieke constraints zijn actief. |
| Versies | Alleen inhoudelijke wijzigingen maken een nieuwe versie; alleen een andere ophaaltijd niet. Een lijstresponse verarmt geen volledige detailtekst. |
| Cache | Verse hit, TTL-verloop en force-refresh getest met controleerbare klok en bronaantallen. Cachetijd wordt niet als actuele broncontrole gepresenteerd. |
| Authenticatie | Uitloggen/herlogin blijft technisch aantoonbaar; cache maskeert geen verlopen sessie. Geen geheimen in database, fixtures, stdout of logs. |
| Tekstzoeken | Representatieve vacatures vinden op C#, .NET en SQL Server; leestekens en onverwachte zoeksyntaxis veroorzaken geen fout of onbedoeld brede match. Resultaten verwijzen naar de juiste bron-ID's. |
| Brede verzameling | Ook ontwikkelaars- en architectuurrollen zonder C#/.NET in titel of snippet krijgen een detailtekst. Een shortlistlimiet of FTS-selectie beperkt niet ongemerkt de verzameling. |
| Corpus en analyse | Stabiele batchgewijze opsomming verwerkt ieder lid precies herleidbaar, ook bij gelijktijdige verversing. Ontbrekende details en nog niet geanalyseerde tekstversies blijven zichtbaar. Samenvattingen behouden versie en bewijs; gewijzigde teksten en nieuwe vragen kunnen herlezing vereisen. |
| Index en transacties | Updates/deletes en herbouw geven dezelfde inhoud; geforceerde mislukking laat geen halve mutatie achter. Gelijktijdige writes verliezen geen relaties. |
| Herkomst/dubbelen | Alle links en bewezen herkomst blijven zichtbaar; gelijke titel alleen leidt niet tot samenvoegen; tegenstrijdige publicatievelden blijven afzonderlijk. |
| Bronfouten en dekking | Login-HTML, foutobjecten, rate limits en contractwijzigingen zijn geen lege successen. Bronstatus, onvolledige paginering en deelresultaten blijven expliciet. |
| Migratie en beheer | Bestaande database migreert atomair; nieuwere onbekende versie geeft duidelijke fout; bewaarbeleid en opschoning behouden referentiële consistentie. Geen live databases in git. |

Voer relevante offline tests en een MCP-toolcontractcontrole uit. Live bronproeven volgen alleen binnen de daarvoor afgesproken handmatige sessies. De huidige bekende zoeklessen blijven gelden: breed op rollen zoeken, synoniemen gebruiken, volledige teksten vóór matching lezen en SQL-beheer als afzonderlijke richting beoordelen.

## Eerstvolgende stap

De eerste opslagstap is geïmplementeerd en offline geverifieerd. Vervolg met cache/corpus/FTS volgens fase 3, en op aanwijzing van de gebruiker de eerste nieuwe bron samen handmatig openen om de backendflow te onderzoeken. Nieuwe adapters vereisen nog steeds hun eigen waargenomen zoek- en detailcontract.


### Flextender en Gelderland — 6 november 2026

Flextender is als publieke adapter toegevoegd met volledige detailteksten, provincie, dynamische formuliervelden, alle client-side lijstpagina's en hervatbare collectie met cachehergebruik. De gebruiker gaf toestemming de publieke route te gebruiken in plaats van de ingelogde aanbevelingen. Plaats-/kilometerfilters worden expliciet afgewezen; exacte standplaats blijft voor zover vermeld in de volledige tekst. Zie [broncontract en verificatie](../../docs/flextender.md). Werken in Gelderland is een kandidaatbron: de officiële inhuuruitleg bevestigt een overstap naar Flextender op 3 april 2025. Het eigen inhuuroverzicht is nog geen geïmplementeerde adapter. De rol van opdrachtoverheid.nl is niet vastgesteld.


### Accountvrije bronneninventarisatie — 6 november 2026

Op expliciet verzoek tijdens afwezigheid van de gebruiker zijn zeventien bronnen geïnventariseerd, met gewone HTTP en alleen waar nodig headless Chrome. Geen computerbesturing of nieuwe productieadapters. Freep, MiPublic en Magnit leveren bewezen accountvrije lijst- en detailroutes. Magnit leverde twee anonieme cursorpagina's zonder overlap. LibLab en Werken in Gelderland zijn ook eenvoudig toegankelijk. Accountgrenzen en blokkades zijn vastgelegd; Circle8 is na eerdere herhaalde 403-fouten niet opnieuw geprobeerd. Zie [inventarisatie en bouwadvies](../../docs/bronneninventarisatie-2026-11-06.md) en [gestructureerd overzicht](../../docs/bronneninventarisatie-2026-11-06.json).


### Vijf openbare bronadapters gebouwd — 6 november 2026

Op expliciete vervolgopdracht zijn Freep, MiPublic, Magnit, LibLab en Werken in Gelderland geïmplementeerd en geregistreerd. Alle vijf zijn via anonieme HTTP live geverifieerd met volledige tekst, opgeslagen inventaris, cachehergebruik en hervatting via een nieuwe service/store-instantie. MiPublic omvat alle vakgebieden, Magnit is wereldwijd. LibLab verwerkt native pagina's omdat gecachete totalen verschillen. Lokale zoektermen gelden voor de huidige volledig gelezen bronpagina; geen kilometerfilter of bronglobale lokale tekstzoektool geclaimd. Bestaande gedeelde gegevens worden nooit door scans verwijderd. Zie [implementatie en verificatie](../../docs/public-source-adapters.md).
