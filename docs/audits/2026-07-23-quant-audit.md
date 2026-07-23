# AI Advisor — Quantitative Audit #1 (2026-07-23 replay session)

**Method.** Every buy/sell verdict in `analyses.jsonl` (records #1-#20) was scored against
subsequent price action reconstructed from the 30-bar context windows of later records in
the same replay session group (bar-level, 1-minute granularity). Fill logic: limit fills on
touch; conservative convention (stop+target in the same bar = stop, unless bar-path logic
resolves the order). Every resolved outcome below was hand-verified against the raw bars.

## Signals and outcomes

| # | Señal | Entry | Stop | Target | Resultado | R | Verificación |
|---|-------|-------|------|--------|-----------|---|--------------|
| 7 | SHORT (55) | 29082.00 | 29100.00 | 29040.00 | **STOP** | -1.0 | Fill 20:56; fue 29 pts a favor (L 29053) sin tocar target (42 pts); stop 21:03 (H 29103.75) |
| 8 | SHORT (58) | 29018.00 | 29031.00 | 28960.00 | **STOP** | -1.0 | Fill y stop 20:48 (H 29033); el squeeze siguió hasta 29105 |
| 10 | SHORT (65) | 28978.00 | 28993.00 | 28935.00 | **SIN DATOS** | — | 0 min de tape tras el click (fin de grupo de sesión) |
| 11 | BUY (62) | 29668.00 | 29654.00 | 29696.00 | **STOP** | -1.0 | Vela 09:31 (O29710 L29651): el recorrido al low atraviesa entry→stop en secuencia — stop cierto |
| 12 | SHORT (58) | 29605.00 | 29623.00 | 29562.00 | **SIN DATOS** | — | 4 min de cobertura, sin toque |
| 14 | SHORT (63) | 29419.00 | 29452.25 | 29363.00 | **STOP** | -1.0 | Fill 20:13; stop 20:15 (H 29468.75); DESPUÉS cayó a 29394 — idea correcta, barrida antes |
| 16 | BUY (62) | 29456.00 | 29435.50 | 29500.00 | **STOP** | -1.0 | Fill 20:40; stop 20:43 (L 29428.25) |
| 17 | BUY (62) | 29516.00 | 29494.50 | 29560.00 | **STOP** | -1.0 | Fill 21:01; stop 21:14 (L 29488) |
| 18 | SHORT (55) | 29479.75 | 29504.00 | 29457.00 | **ABIERTA** | -0.4 | Trigger break-below; sin resolver al fin de datos (últ. close 29489.5), 17 min cobertura |
| 19 | SHORT (62) | 29529.50 | 29542.00 | 29491.00 | **SIN DATOS** | — | 4 min de cobertura |

**Resueltas: 6 fills → 6 stops, 0 targets. R realizado: -6.0R (+ -0.4R abierta).**
**En dólares (NQ, 1 contrato): -$2,405 realizados (+ -$195 la abierta) — habría quemado la
evaluación de $2,000 en la 5ª-6ª señal.**

## Diagnóstico del patrón (por qué 0/6)

1. **Las 6 perdedoras fueron entradas límite contra-rotación (vender rebote / comprar dip)
   en una sesión de velocidad extrema** — velas de 1 min de 25-86 puntos. Una orden límite
   contra el impulso se llena exactamente cuando el momentum atraviesa el nivel con más
   fuerza (selección adversa), y stops de 13-33 pts quedan dentro del ruido de UNA vela en
   ese régimen.
2. **El sistema pre-recalibración vetaba exactamente estos trades por exactamente esta
   razón** ("stops dentro del ruido de barra"). El giro a "más agresivo" quitó ese freno y
   esta cinta cobró la matrícula completa. En régimen de volatilidad extrema, aquellos HOLD
   eran información, no cobardía.
3. **La confianza — dato sugerente pero NO concluyente**: todas las perdedoras salieron con
   55-65 y un filtro ≥70 habría tomado cero trades. PERO no hubo ganadoras en la muestra
   (0/6), así que cualquier filtro restrictivo "habría evitado las pérdidas" — el dato no
   prueba que la confianza discrimine. Además, en todo el log el modelo nunca ha emitido
   una señal de trade >65 (las confianzas altas, 80/65, fueron de holds): si la escala vive
   comprimida en 55-65 para trades, un umbral ≥70 sería un apagado, no un filtro. Queda en
   observación para la próxima auditoría (¿discrimina entre ganadoras y perdedoras? ¿cruza
   alguna vez 70 en cinta normal?).
4. Nota a favor del motor de niveles: #7 y #14 eran ideas direccionales correctas (el precio
   acabó yendo a la zona del target) — murieron por timing/stop en el squeeze, no por lado.

## Caveats

- n=6 resueltas, UNA sesión, UN régimen (replay de volatilidad extrema): esto audita el
  comportamiento en ese régimen, no el valor del advisor en general.
- Señales correlacionadas (misma cinta, misma dirección de error) — no son muestras
  independientes.
- Granularidad de 1 minuto: convención conservadora en velas ambiguas (solo #8 la necesitó;
  su extensión posterior lo hace inambiguo).
- 3 señales sin datos posteriores (fin de grupo de sesión) — no puntuables.

## Recomendaciones (decisión del trader, no auto-aplicadas)

- **A (prompt, lente de riesgo):** regla de stop escalada a volatilidad — el stop debe
  quedar ≥1.5× el rango medio de las últimas 10 velas; si con eso ningún nivel cercano da
  R:R ≥1.5, el hold es correcto y viene justificado por números del régimen.
- **B (regla de ejecución del trader, sin código):** operar solo señales con confianza ≥70.
  **NO validada aún** — sin ganadoras en la muestra el dato es trivial, y el modelo nunca ha
  emitido >65 en un trade (posible escala comprimida). No adoptar hasta que la próxima
  auditoría muestre que la confianza discrimina.
- **C:** A ahora + confianza en observación; activar B después solo si los datos de la
  siguiente auditoría la respaldan, con umbral elegido por datos.

Próxima auditoría: tras ~10 señales en régimen de volatilidad normal.
