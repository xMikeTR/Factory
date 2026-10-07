package factory.pobserve;

import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;
import java.util.HashMap;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;

import pobserve.commons.PObserveEvent;
import pobserve.commons.Parser;
import pobserve.runtime.events.PEvent;

/**
 * The factory's logging contract, read generically: a log line carrying {@code pobserve <event> [payload]}
 * becomes that P event, looked up by name in the spec's generated {@code PEvents}. Payloads are scalars
 * (int, bool, string); an event the spec does not observe is not generated and so is skipped, as is any
 * line without the marker. A marked line that does not fit its event is an error, never a silent skip.
 * Events are keyed into one partition and ordered by their position in the log.
 */
public class EventLineParser implements Parser<PEvent<?>> {
    private static final Pattern LINE = Pattern.compile("(?:^|\\s)pobserve\\s+([A-Za-z_]\\w*)(?:\\s+(.*\\S))?\\s*$");
    private static final String KEY = "run";

    private String eventsClass = "FactorySpec.pobserve.PEvents";
    private long position;
    private transient Map<String, Constructor<?>> constructors;

    /** The generated PEvents class name, `<ProjectName>.pobserve.PEvents`. */
    @Override
    public void setConfiguration(String configuration) {
        if (configuration != null && !configuration.isBlank()) eventsClass = configuration.trim();
    }

    @Override
    public Stream<PObserveEvent<PEvent<?>>> apply(Object logLine) {
        String line = String.valueOf(logLine);
        Matcher m = LINE.matcher(line);
        if (!m.find()) return Stream.empty();
        Constructor<?> ctor = constructorFor(m.group(1));
        if (ctor == null) return Stream.empty();
        PEvent<?> event = construct(ctor, m.group(1), m.group(2), line);
        return Stream.of(new PObserveEvent<>(KEY, ++position, event, line));
    }

    private Constructor<?> constructorFor(String name) {
        if (constructors == null) constructors = new HashMap<>();
        return constructors.computeIfAbsent(name, n -> {
            try {
                Class<?> c = Class.forName(eventsClass + "$" + n, true, getClass().getClassLoader());
                if (!PEvent.class.isAssignableFrom(c)) return null;
                Constructor<?>[] cs = c.getConstructors();
                return cs.length == 1 ? cs[0] : null;
            } catch (ClassNotFoundException e) {
                return null;
            }
        });
    }

    private static PEvent<?> construct(Constructor<?> ctor, String name, String payload, String line) {
        Class<?>[] params = ctor.getParameterTypes();
        try {
            if (params.length == 0) {
                if (payload != null) throw bad(name, "takes no payload", line);
                return (PEvent<?>) ctor.newInstance();
            }
            if (params.length != 1 || payload == null) throw bad(name, "needs one payload", line);
            return (PEvent<?>) ctor.newInstance(value(params[0], name, payload, line));
        } catch (InstantiationException | IllegalAccessException | InvocationTargetException e) {
            throw new IllegalArgumentException("pobserve: cannot construct " + name + ": " + e, e);
        }
    }

    private static Object value(Class<?> type, String name, String payload, String line) {
        try {
            if (type == long.class || type == Long.class) return Long.parseLong(payload);
            if (type == boolean.class || type == Boolean.class) {
                if (!payload.equals("true") && !payload.equals("false")) throw new NumberFormatException(payload);
                return Boolean.parseBoolean(payload);
            }
            if (type == String.class) return payload.length() >= 2 && payload.startsWith("\"") && payload.endsWith("\"")
                    ? payload.substring(1, payload.length() - 1) : payload;
        } catch (NumberFormatException e) {
            throw bad(name, "payload '" + payload + "' is not a " + type.getSimpleName(), line);
        }
        throw bad(name, "payload type " + type.getSimpleName() + " is not supported (scalars only)", line);
    }

    private static IllegalArgumentException bad(String name, String why, String line) {
        return new IllegalArgumentException("pobserve: event " + name + " " + why + ": " + line);
    }
}
