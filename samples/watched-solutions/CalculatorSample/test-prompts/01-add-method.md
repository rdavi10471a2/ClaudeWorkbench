# 01 — Add a method (single file, surgical)

**Tests:** a one-file change via a typed symbol edit, a single-file session, and one Accept
that writes immediately (single-file sessions are terminal on the first Accept).

## Prompt

Add a `Modulo(double a, double b)` method to the `Calculator` class that returns the
remainder of `a` divided by `b`. Match `Divide`'s guard: throw a `DivideByZeroException`
with a clear message when `b` is zero. Place it right after `Divide`.
