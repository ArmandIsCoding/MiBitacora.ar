namespace MiBitacora.Services;

public sealed record ReportSectionDefinition(string Id, string Title, string Purpose);
public sealed record ReportSectionPlan(string Id, IReadOnlyList<string> SourceIds);
public sealed record TherapyReportPoint(string Kind, string Title, string Text, string Question, IReadOnlyList<TopicEvidence> Evidence);
public sealed record TherapyReportSection(string Id, string Title, string Introduction, IReadOnlyList<TherapyReportPoint> Points);
public sealed record TherapyReport(int Version, DateTime? FromUtc, DateTime? ThroughUtc,
    DateTime RecentSinceUtc, int RecentEntryCount, IReadOnlyList<TherapyReportSection> Sections);

public interface IJournalReportAnalyzer
{
    bool IsConfigured { get; }
    bool UsesExternalProvider { get; }
    Task<IReadOnlyList<ReportSectionPlan>> PlanReportAsync(IReadOnlyList<MemoryCard> cards, DateTime nowUtc, CancellationToken ct);
    Task<TherapyReportSection> WriteReportSectionAsync(ReportSectionDefinition section, IReadOnlyList<TopicSource> sources,
        string scope, CancellationToken ct);
}

public static class JournalReportSections
{
    public static readonly IReadOnlyList<ReportSectionDefinition> All =
    [
        new("panorama", "Mi momento reciente", "Panorama integrado de los últimos registros: malestar, bienestar, ambivalencias, funcionamiento y preocupaciones explícitas. Diferenciá los últimos 30 días del pasado. Si faltan registros recientes, no afirmes cómo estoy hoy."),
        new("recorrido", "Mi recorrido en el tiempo", "Hitos, transiciones y cambios a través de todo el período, en orden cronológico y con fechas concretas. No atribuyas continuidad a silencios del diario. Incluí cambios de opinión y etapas de mejora, no una lista de cada entrada."),
        new("emociones", "Emociones y necesidades", "Qué emociones nombré, en qué circunstancias, qué necesidades expresé y cuáles podrían explorarse como hipótesis. Diferenciá sentimientos explícitos de lecturas posibles. Incluí emociones agradables y contradictorias."),
        new("conexiones", "Situaciones y conexiones posibles", "Circunstancias que aparecen junto a emociones y posibles factores que conviene explorar. Cada explicación es hipótesis, no causalidad demostrada; ofrecé alternativas y qué información permitiría aclararlas. No inventes trauma, causas inconscientes ni motivos ajenos."),
        new("patrones", "Repeticiones y cambios", "Patrones posibles en pensamientos, acciones o relaciones, con al menos dos episodios distintos para hablar de repetición. Incluí excepciones y cambios que cuestionen la lectura. Una frase como siempre no prueba frecuencia. No etiquetes rasgos fijos."),
        new("vinculos", "Vínculos, límites y entorno", "Cómo describí vínculos, conflictos, límites, trabajo y contexto cotidiano; apoyos, tensiones y ambivalencias con ejemplos. No diagnostiques a terceros ni decidas por mí sobre mis vínculos."),
        new("recursos", "Recursos que puedo reconocer", "Apoyos humanos, actividades, valores, capacidades, decisiones y momentos de disfrute documentados. Qué pareció ayudar según mis propias palabras; no inventes resiliencia ni minimices dolor con optimismo genérico."),
        new("prioridades", "Por dónde empezar en terapia", "Tres a cinco asuntos concretos para priorizar con mi terapeuta. Explicá por qué cada uno merece espacio, qué me gustaría comprender y una pregunta de apertura. Son propuestas para acordar, no órdenes, tratamientos ni una clasificación clínica de urgencia."),
        new("preguntas", "Preguntas para abrir la conversación", "Preguntas específicas que conecten fuentes distantes y permitan profundizar, sin sugerir respuestas. Posibles observaciones a registrar o ejemplos a llevar, siempre opcionales. Evitá repetir las mismas preguntas de prioridades."),
        new("incertidumbres", "Lo que todavía falta comprender", "Vacíos reales, contradicciones, límites de lo escrito y aspectos que habría que preguntar en terapia. No conviertas la falta de datos en síntomas ausentes ni presentes. No inventes antecedentes, frecuencia, duración, impacto, diagnóstico o estado mental actual.")
    ];
}
