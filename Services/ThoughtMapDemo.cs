namespace MiBitacora.Services;

/// <summary>Fictional, deliberately public example. Never populated with account data.</summary>
public static class ThoughtMapDemo
{
    public static ThoughtMapData Create() => new(
    [
        new("t-creativity", "theme", "Volver a crear", "Brisa propuso este tema al leer las entradas de este ejemplo."),
        new("t-time", "theme", "Un tiempo para mí", "Un tema que conecta pequeños momentos de la semana, en este ejemplo ficticio."),
        new("t-change", "theme", "Animarme al cambio", "Una posibilidad para explorar y conversar en terapia."),
        new("e-sketch", "entry", "Hoy volví a dibujar", "Abrí el cuaderno que tenía guardado. Dibujé diez minutos y me hizo bien. No necesitaba que saliera perfecto.", Notebook: "Mi diario"),
        new("e-music", "entry", "Esa canción que me acompañaba", "Escuché una canción de cuando estudiaba. Me dieron ganas de volver a tocar la guitarra.", Notebook: "Pequeños descubrimientos"),
        new("e-walk", "entry", "Salí a caminar sin apuro", "Dejé el teléfono en casa y salí a caminar. Aparecieron ideas que hacía mucho no escuchaba.", Notebook: "Mi diario"),
        new("e-no", "entry", "Hoy pude decir que no", "Elegí quedarme en casa un rato. Me costó explicarlo, pero necesitaba ese espacio para mí.", Notebook: "Mi diario"),
        new("e-class", "entry", "Me anoté en ese taller", "Después de pensarlo tanto, me anoté en un taller de dibujo. Siento nervios y también entusiasmo.", Notebook: "Pequeños descubrimientos"),
        new("e-first", "entry", "Un pequeño primer paso", "Dejé los lápices sobre la mesa. Quiero que crear sea una parte de mi día, aunque sea un ratito.", Notebook: "Mi diario"),
        new("q-create", "question", "¿Qué te gustaría volver a hacer?", "Una pregunta de Brisa que abrió una nueva rama de la conversación."),
        new("q-time", "question", "¿Cómo sería darte ese espacio?", "Una pregunta para seguir pensando entre sesiones."),
        new("q-change", "question", "¿Qué te entusiasma de empezar?", "Una pregunta para retomar en tu próxima sesión de terapia."),
        new("n-session", "note", "Quiero llevar esto a terapia", "Conversar sobre por qué necesito que todo salga bien antes de empezar."),
    ],
    [
        new("e-sketch", "t-creativity", "theme", "Brisa propuso este tema"),
        new("e-music", "t-creativity", "theme", "Brisa propuso este tema"),
        new("e-class", "t-creativity", "theme", "Brisa propuso este tema"),
        new("e-first", "t-creativity", "theme", "Brisa propuso este tema"),
        new("e-walk", "t-time", "theme", "Brisa propuso este tema"),
        new("e-no", "t-time", "theme", "Brisa propuso este tema"),
        new("e-sketch", "t-time", "theme", "Brisa propuso este tema"),
        new("e-class", "t-change", "theme", "Brisa propuso este tema"),
        new("e-no", "t-change", "theme", "Brisa propuso este tema"),
        new("e-sketch", "q-create", "question", "Brisa dejó esta pregunta"),
        new("q-create", "e-music", "reply", "Respondiste y abriste una rama"),
        new("q-create", "e-first", "reply", "Respondiste y abriste una rama"),
        new("e-walk", "q-time", "question", "Brisa dejó esta pregunta"),
        new("q-time", "e-no", "reply", "Respondiste y abriste una rama"),
        new("e-class", "q-change", "question", "Brisa dejó esta pregunta"),
        new("e-sketch", "e-class", "context", "Brisa consultó este antecedente"),
        new("e-walk", "e-first", "context", "Brisa consultó este antecedente"),
        new("n-session", "e-class", "context", "Brisa consultó esta notita"),
    ], 6, 6, false);
}
