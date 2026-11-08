# GEAL: the script language in the on_xxx = { ... } blocks of .ot/.otc/.ao/.aoc/.it files.
#
# The grammar follows the client's compiler: statements 0x141df8210, assignment 0x141df8780, local 0x141df91a0,
# if 0x141df9680, expression 0x141dfa050, binary operators 0x141dfa8c0, unary 0x141dfa770, lambda/argument block
# 0x141dea480, [x, y] Location constant 0x141dea190, the argument list 0x141dfd380.
# Binary precedence comes from the table at 0x142fc8558 (token types 0..13).
#
# parse() returns plain dicts and lists, so the result goes straight to JSON. source() prints a tree back as GEAL;
# decode_all.py compares its tokens with the original's, so a parser that drops anything fails loudly.
import re

TOKEN = re.compile(r'''
    (?P<ws>\s+|//[^\r\n]*|/\*.*?\*/)
  | (?P<str>"[^"]*"|'[^']*')
  | (?P<num>\d+\.\d*(?:[eE][+-]?\d+)?f?|\.\d+(?:[eE][+-]?\d+)?f?|\d+(?:[eE][+-]?\d+)?f?)
  | (?P<id>[A-Za-z_][A-Za-z0-9_]*)
  | (?P<op>\[\+\]|\[-\]|==|!=|<=|>=|\+=|-=|\|\||&&|[-+*/<>=!&|^%(){}\[\],;.:?~])
''', re.X | re.S)

# Lower binds looser. 0x142fc8558: 3 3 4 4 4 4 6 6 7 7 5 5 1 2 for == != < > <= >= + - * / [+] [-] || &&
BINARY = {"||": 1, "&&": 2, "==": 3, "!=": 3, "<": 4, ">": 4, "<=": 4, ">=": 4, "[+]": 5, "[-]": 5, "+": 6, "-": 6,
          "*": 7, "/": 7}
UNARY = {"-", "!"}
ASSIGN = {"=", "+=", "-="}


class GealError(Exception):
    pass


def tokenize(text):
    out, i, n = [], 0, len(text)
    while i < n:
        m = TOKEN.match(text, i)
        if not m:
            raise GealError(f"bad character {text[i]!r} at {i}: {text[max(0, i - 30):i + 30]!r}")
        i = m.end()
        if m.lastgroup != "ws":
            out.append((m.lastgroup, m.group()))
    return out


class Parser:
    def __init__(self, tokens):
        self.t = tokens
        self.i = 0

    def peek(self, k=0):
        j = self.i + k
        return self.t[j][1] if j < len(self.t) else None

    def kind(self, k=0):
        j = self.i + k
        return self.t[j][0] if j < len(self.t) else None

    def at(self, text):
        # A string token whose text is an operator must not match, so only op and id tokens compare.
        return self.i < len(self.t) and self.t[self.i][0] in ("op", "id") and self.t[self.i][1] == text

    def next(self):
        if self.i >= len(self.t):
            raise GealError("unexpected end of script")
        self.i += 1
        return self.t[self.i - 1][1]

    def expect(self, text):
        if not self.at(text):
            raise GealError(f"expected {text!r}, found {self.context()}")
        self.i += 1

    def context(self):
        return repr(" ".join(t[1] for t in self.t[max(0, self.i - 6):self.i + 6]))

    def statements(self, end):
        out = []
        while not (self.at(end) if end else self.i >= len(self.t)):
            if self.i >= len(self.t):
                raise GealError(f"missing {end!r}")
            out.append(self.statement())
        return out

    def block(self):
        self.expect("{")
        body = self.statements("}")
        self.expect("}")
        return {"t": "block", "body": body}

    def statement(self):
        if self.at("{"):
            return self.block()
        if self.at(";"):
            self.next()
            return {"t": "empty"}
        if self.at("if"):
            self.next()
            self.expect("(")
            cond = self.expression()
            self.expect(")")
            node = {"t": "if", "cond": cond, "then": self.statement()}
            if self.at("else"):
                self.next()
                node["else"] = self.statement()
            return node
        if self.at("local"):
            self.next()
            node = {"t": "local", "name": self.next()}
            if self.peek() in ASSIGN and self.kind() == "op":
                node["op"] = self.next()
                node["value"] = self.expression()
            self.expect(";")
            return node
        if self.at("return"):
            self.next()
            node = {"t": "return"}
            if not self.at(";"):
                node["value"] = self.expression()
            self.expect(";")
            return node
        e = self.expression()
        if self.kind() == "op" and self.peek() in ASSIGN:
            op = self.next()
            node = {"t": "assign", "target": e, "op": op, "value": self.expression()}
        else:
            node = {"t": "expr", "value": e}
        self.expect(";")
        return node

    def expression(self, level=0):
        left = self.unary()
        while self.kind() == "op" and BINARY.get(self.peek(), -1) >= level and self.peek() in BINARY:
            op = self.next()
            right = self.expression(BINARY[op] + 1)
            left = {"t": "bin", "op": op, "l": left, "r": right}
        return left

    def unary(self):
        if self.kind() == "op" and self.peek() in UNARY:
            op = self.next()
            return {"t": "un", "op": op, "v": self.unary()}
        return self.postfix(self.primary())

    def is_lambda(self):
        # "(" name? ")" "{" or a parameter list: 0x141dfa050 looks at the next tokens for ")" (token 0x1d).
        j = self.i + 1
        while j < len(self.t) and (self.t[j][0] == "id" or self.t[j][1] == ","):
            j += 1
        return j + 1 < len(self.t) and self.t[j][1] == ")" and self.t[j][0] == "op" and self.t[j + 1][1] == "{"

    def primary(self):
        k, v = self.kind(), self.peek()
        if k is None:
            raise GealError("expected a value, found the end of the script")
        if k == "num":
            self.next()
            return {"t": "float" if ("." in v or "e" in v.lower()) else "int", "v": v}
        if k == "str":
            self.next()
            return {"t": "str", "v": v}
        if k == "id":
            self.next()
            return {"t": "name", "v": v}
        if v == "(":
            if self.is_lambda():
                self.next()
                params = []
                while not self.at(")"):
                    params.append(self.next())
                    if self.at(","):
                        self.next()
                self.next()
                return {"t": "lambda", "params": params, "body": self.block()}
            self.next()
            e = self.expression()
            self.expect(")")
            return {"t": "paren", "v": e}
        if v == "[":
            # [x, y] Location constant, 0x141dea190; the client wants integers but the scripts also negate them.
            self.next()
            x = self.expression()
            self.expect(",")
            y = self.expression()
            self.expect("]")
            return {"t": "location", "x": x, "y": y}
        if v == "{":
            return self.block()
        raise GealError(f"unexpected token, at {self.context()}")

    def arguments(self):
        """After "(", through ")". Built-ins get their arguments as token ranges (FuncWrapper takes a
        StackVector<Range<ScriptToken>>) and read them as they like, so <root>, = 0 and > -1 are arguments too: what is
        not an expression stays a list of raw tokens."""
        args = []
        while not self.at(")"):
            end = self.argument_end()
            start = self.i
            try:
                sub = Parser(self.t[start:end])
                value = sub.expression()
                if sub.i != end - start:
                    raise GealError("more tokens after the value")
            except GealError:
                if self.t[start][1] == "{" and self.t[start][0] == "op":
                    raise  # a code block has to parse
                value = {"t": "raw", "tokens": [x[1] for x in self.t[start:end]]}
            args.append(value)
            self.i = end
            if self.at(","):
                self.next()
                if self.at(")"):
                    args.append({"t": "raw", "tokens": []})  # the client accepts a comma before ")"
        self.next()
        return args

    def argument_end(self):
        depth, j = 0, self.i
        while j < len(self.t):
            k, v = self.t[j]
            if k == "op":
                if v in ("(", "{", "["):
                    depth += 1
                elif v in (")", "}", "]"):
                    if depth == 0:
                        return j
                    depth -= 1
                elif v == "," and depth == 0:
                    return j
            j += 1
        raise GealError("missing ')' after the arguments")

    def postfix(self, e):
        while self.kind() == "op":
            if self.at("."):
                self.next()
                e = {"t": "member", "of": e, "name": self.next()}
            elif self.at("["):
                self.next()
                idx = self.expression()
                self.expect("]")
                e = {"t": "index", "of": e, "i": idx}
            elif self.at("("):
                self.next()
                e = {"t": "call", "fn": e, "args": self.arguments()}
                # A call may be followed by a code block: Do(...) { ... } is sugar the client accepts for a block argument.
                if self.at("{"):
                    e["block"] = self.block()
            else:
                break
        return e


def parse(text):
    """A script's source, without its outer braces -> the list of its statements."""
    p = Parser(tokenize(text))
    return p.statements(None)


def expr_source(e):
    t = e["t"]
    if t in ("int", "float", "str", "name"):
        return e["v"]
    if t == "raw":
        return " ".join(e["tokens"])
    if t == "paren":
        return "(" + expr_source(e["v"]) + ")"
    if t == "bin":
        return f'{expr_source(e["l"])} {e["op"]} {expr_source(e["r"])}'
    if t == "un":
        return e["op"] + expr_source(e["v"])
    if t == "member":
        return f'{expr_source(e["of"])}.{e["name"]}'
    if t == "index":
        return f'{expr_source(e["of"])}[{expr_source(e["i"])}]'
    if t == "location":
        return f'[{expr_source(e["x"])}, {expr_source(e["y"])}]'
    if t == "call":
        s = f'{expr_source(e["fn"])}({", ".join(expr_source(a) for a in e["args"]).rstrip()})'
        if "block" in e:
            s += " " + block_source(e["block"], 0)
        return s
    if t == "lambda":
        return f'({", ".join(e["params"])}) {block_source(e["body"], 0)}'
    if t == "block":
        return block_source(e, 0)
    raise GealError(f"cannot print {t}")


def block_source(b, depth):
    if not b["body"]:
        return "{ }"
    pad = "\t" * (depth + 1)
    return "{\n" + "".join(stmt_source(s, depth + 1, pad) for s in b["body"]) + "\t" * depth + "}"


def stmt_source(s, depth, pad):
    t = s["t"]
    if t == "block":
        return pad + block_source(s, depth) + "\n"
    if t == "empty":
        return pad + ";\n"
    if t == "if":
        out = f'{pad}if ({expr_source(s["cond"])})\n{stmt_source(s["then"], depth, pad)}'
        if "else" in s:
            out += f'{pad}else\n{stmt_source(s["else"], depth, pad)}'
        return out
    if t == "local":
        return f'{pad}local {s["name"]}' + (f' {s["op"]} {expr_source(s["value"])}' if "op" in s else "") + ";\n"
    if t == "return":
        return f'{pad}return' + (f' {expr_source(s["value"])}' if "value" in s else "") + ";\n"
    if t == "assign":
        return f'{pad}{expr_source(s["target"])} {s["op"]} {expr_source(s["value"])};\n'
    if t == "expr":
        return f'{pad}{expr_source(s["value"])};\n'
    raise GealError(f"cannot print {t}")


def source(statements):
    return "".join(stmt_source(s, 0, "") for s in statements)


def walk(node):
    """Every dict in a tree, the node first."""
    if isinstance(node, dict):
        yield node
        for v in node.values():
            yield from walk(v)
    elif isinstance(node, list):
        for v in node:
            yield from walk(v)
