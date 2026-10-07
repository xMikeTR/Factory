package factory.pobserve;

import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;

import pobserve.commons.PObserveEvent;
import pobserve.commons.Parser;
import pobserve.runtime.events.PEvent;

/**
 * The factory's logging contract, read generically: a log line carrying {@code pobserve <event> [payload] [key=<id>]}
 * becomes that P event, looked up by name in the spec's generated {@code PEvents}. Payloads are scalars
 * (int, bool, string); an event the spec does not observe is not generated and so is skipped, as is any
 * line without the marker. A marked line that does not fit its event is an error, never a silent skip.
 * The key names the entity whose history the spec follows (one spec instance per key, e.g. one per
 * quote); without one, every event is in the single partition "run". Events are ordered by position.
 */
public class EventLineParser implements Parser<PEvent<?>> {
    private static final Pattern LINE = Pattern.compile("(?:^|\\s)pobserve\\s+([A-Za-z_]\\w*)(?:\\s+(.*\\S))?\\s*$");
    private static final Pattern KEYED = Pattern.compile("^(?:(.*\\S)\\s+)?key=(\\S+)$");
    private static final String NO_KEY = "run";

    private String eventsClass = "FactorySpec.pobserve.PEvents";
    private final AtomicLong position = new AtomicLong();
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
        String payload = m.group(2), key = NO_KEY;
        Matcher k = payload == null ? null : KEYED.matcher(payload);
        if (k != null && k.matches()) {
            payload = k.group(1);
            key = k.group(2);
        }
        PEvent<?> event = construct(ctor, m.group(1), payload, line);
        return Stream.of(new PObserveEvent<>(key, position.incrementAndGet(), event, line));
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
