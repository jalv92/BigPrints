# AI Advisor — Quantitative Audit #2 (first session with auto outcome tracking)

**Method.** Direct read of `type: outcome` records written by the tick-based signal
tracker (built after audit #1 closed its coverage blind spot). No reconstruction, no
coverage caveats: every signal is scored against the ticks the position actually saw.
Volatility guard (rule A) active throughout.

## Results

| signal_ts | Señal | Conf | Entry | Stop | Target | Resultado | R |
|---|---|---|---|---|---|---|---|
| 16:30:25 | SHORT | 58 | 29780.00 | 29800.00 | 29750.00 | no_fill (superseded) | — |
| 16:31:41 | SHORT | 68 | 29753.00 | 29774.00 | 29700.00 | stop | -1.00 |
| 16:33:39 | SHORT | 62 | 29806.50 | 29830.00 | 29768.00 | **target** | +1.64 |
| 16:37:55 | SHORT | 62 | 29768.00 | 29782.00 | 29739.00 | stop | -1.00 |
| 16:39:35 | SHORT | 62 | 29747.00 | 29765.00 | 29717.00 | **target** | +1.67 |

**4 resueltas: 2 target / 2 stop (50%), +1.30R total, +0.33R medio, ≈ +$499 a 1 NQ
(riesgo medio 19.1 pts — dentro de la banda 15-50 del guardarraíl).** Breakeven con
ganadoras ~1.65R = ~38% de acierto; 50% observado.

## Preguntas pre-registradas

1. **Win rate con el guardarraíl activo:** 50%, expectativa positiva. Primera sesión
   rentable. **n=4 — evidencia inicial, no veredicto.**
2. **¿La confianza discrimina?** **NO — evidencia en contra:** perdedoras promedian
   confianza más alta que las ganadoras (65 vs 62); la señal de mayor confianza del
   log completo (68) fue stop. Un filtro ≥65 habría tomado solo la perdedora del 68 y
   saltado ambas ganadoras. **Regla B rechazada por datos** — la confianza queda como
   dato informativo, sin regla de ejecución.
3. **¿Cruza 70 alguna vez?** No (máximo histórico: 68). Escala comprimida confirmada.

## Notas

- El tracker automático funcionó en las 5 señales (incl. la superseded) — el punto
  ciego de la auditoría #1 está cerrado.
- Sesgo de muestra: las 5 señales fueron SHORT (cinta direccional bajista). Próximas
  sesiones: variar régimen (día alcista, día de rango) para auditar ambos lados.
- Acumulado histórico (auditorías #1+#2, resueltas sin reservas): 10 señales, 2 target
  / 8 stop, -4.7R — dominado por la sesión de volatilidad extrema pre-guardarraíl. El
  corte relevante es post-regla-A: +1.30R en 4.

**Próximo hito:** ~15-20 señales post-guardarraíl en regímenes variados → decidir si el
edge sostiene y si pasa a ejecución semiautomática vía BigPrintsStrategy.
