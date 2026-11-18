// SPDX-License-Identifier: GPL-3.0-or-later
// A small, tolerant HTML / XML reader and writer for the e-book converters. Web Workers have no DOMParser, so books are read here: a
// tokenizer (tags, attributes, text, comments, CDATA, processing instructions, doctypes) and a tree builder that closes what HTML leaves
// open (void elements, <p> before a block, <li> before the next <li>, table cells...). Entities: the XML ones, numeric references and the
// common HTML names; a DOCTYPE's internal subset is skipped and NEVER expanded (no entity bombs, no external entities). Pure functions.

export interface MText {
  type: "text";
  text: string;
}

export interface MElement {
  type: "el";
  /** HTML: lower case. XML: as written, prefix included ("l:href" is an attribute, "fb:p" an element). */
  name: string;
  attrs: [string, string][];
  children: MNode[];
}

export type MNode = MElement | MText;

export const VOID_ELEMENTS: ReadonlySet<string> = new Set(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"]);
const RAW_TEXT = new Set(["script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes"]);
/** Opening one of these closes an open <p> (HTML). */
const CLOSES_P = new Set([
  "address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3",
  "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
]);

// ---- entities -----------------------------------------------------------------------------------------------------------------------

const NAMED: Record<string, string> = {
  amp: "&", lt: "<", gt: ">", quot: '"', apos: "'", nbsp: " ", shy: "­", ensp: " ", emsp: " ", thinsp: " ",
  zwnj: "‌", zwj: "‍", lrm: "‎", rlm: "‏", ndash: "–", mdash: "—", lsquo: "‘", rsquo: "’",
  sbquo: "‚", ldquo: "“", rdquo: "”", bdquo: "„", dagger: "†", Dagger: "‡", bull: "•", hellip: "…",
  permil: "‰", prime: "′", Prime: "″", lsaquo: "‹", rsaquo: "›", laquo: "«", raquo: "»", euro: "€",
  trade: "™", copy: "©", reg: "®", deg: "°", plusmn: "±", times: "×", divide: "÷", micro: "µ",
  middot: "·", para: "¶", sect: "§", cent: "¢", pound: "£", yen: "¥", curren: "¤", iexcl: "¡",
  iquest: "¿", ordf: "ª", ordm: "º", sup1: "¹", sup2: "²", sup3: "³", frac14: "¼", frac12: "½",
  frac34: "¾", not: "¬", macr: "¯", acute: "´", cedil: "¸", uml: "¨", brvbar: "¦", larr: "←",
  rarr: "→", uarr: "↑", darr: "↓", harr: "↔", minus: "−", infin: "∞", ne: "≠", le: "≤", ge: "≥",
  asymp: "≈", alpha: "α", beta: "β", gamma: "γ", delta: "δ", pi: "π", sigma: "σ", omega: "ω",
  Agrave: "À", Aacute: "Á", Acirc: "Â", Atilde: "Ã", Auml: "Ä", Aring: "Å", AElig: "Æ", Ccedil: "Ç",
  Egrave: "È", Eacute: "É", Ecirc: "Ê", Euml: "Ë", Igrave: "Ì", Iacute: "Í", Icirc: "Î", Iuml: "Ï",
  Ntilde: "Ñ", Ograve: "Ò", Oacute: "Ó", Ocirc: "Ô", Otilde: "Õ", Ouml: "Ö", Oslash: "Ø", Ugrave: "Ù",
  Uacute: "Ú", Ucirc: "Û", Uuml: "Ü", Yacute: "Ý", szlig: "ß", agrave: "à", aacute: "á", acirc: "â",
  atilde: "ã", auml: "ä", aring: "å", aelig: "æ", ccedil: "ç", egrave: "è", eacute: "é", ecirc: "ê",
  euml: "ë", igrave: "ì", iacute: "í", icirc: "î", iuml: "ï", ntilde: "ñ", ograve: "ò", oacute: "ó",
  ocirc: "ô", otilde: "õ", ouml: "ö", oslash: "ø", ugrave: "ù", uacute: "ú", ucirc: "û", uuml: "ü",
  yacute: "ý", yuml: "ÿ", OElig: "Œ", oelig: "œ", Scaron: "Š", scaron: "š", Yuml: "Ÿ", fnof: "ƒ",
  circ: "ˆ", tilde: "˜",
};

function fromCodePoint(n: number): string | null {
  if (!Number.isFinite(n) || n <= 0 || n > 0x10ffff || (n >= 0xd800 && n <= 0xdfff)) return null;
  return String.fromCodePoint(n);
}

/** Decodes character references; an unknown or broken one stays as written. */
export function decodeEntities(text: string): string {
  if (!text.includes("&")) return text;
  return text.replace(/&(#[xX][0-9a-fA-F]{1,8}|#[0-9]{1,9}|[A-Za-z][A-Za-z0-9]{1,31});?/g, (whole, ref: string) => {
    if (ref[0] === "#") {
      const n = ref[1] === "x" || ref[1] === "X" ? parseInt(ref.slice(2), 16) : parseInt(ref.slice(1), 10);
      return fromCodePoint(n) ?? "�";
    }
    return NAMED[ref] ?? whole;
  });
}

export function escapeText(text: string): string {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

export function escapeAttr(text: string): string {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/** Characters XML 1.0 does not allow (C0 controls except tab/newline/return, lone surrogates, U+FFFE/FFFF). */
export function stripInvalidXml(text: string): string {
  return text.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f￾￿]|[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/g, "");
}

// ---- parsing ------------------------------------------------------------------------------------------------------------------------

export interface ParseOptions {
  /** XML: names keep their case, nothing is closed implicitly. HTML (default): tag soup rules. */
  xml?: boolean;
}

const NAME_START = /[A-Za-z_:À-￿]/;
const MAX_DEPTH = 512;

/** Parses markup into a root element named "#document". Never throws on bad markup. */
export function parseMarkup(source: string, options: ParseOptions = {}): MElement {
  const xml = options.xml === true;
  const root: MElement = { type: "el", name: "#document", attrs: [], children: [] };
  const stack: MElement[] = [root];
  const top = () => stack[stack.length - 1];
  const pushText = (text: string) => {
    if (!text) return;
    const parent = top();
    const last = parent.children[parent.children.length - 1];
    if (last && last.type === "text") last.text += text;
    else parent.children.push({ type: "text", text });
  };
  const popTo = (name: string, stopAt: readonly string[] = []): boolean => {
    for (let i = stack.length - 1; i > 0; i--) {
      if (stack[i].name === name) {
        stack.length = i;
        return true;
      }
      if (stopAt.includes(stack[i].name)) return false;
    }
    return false;
  };
  const implicitClose = (name: string) => {
    if (xml) return;
    if (CLOSES_P.has(name) && top().name === "p") stack.pop();
    if (name === "li") popTo("li", ["ul", "ol", "menu"]);
    else if (name === "dt" || name === "dd") {
      if (!popTo("dt", ["dl"])) popTo("dd", ["dl"]);
    } else if (name === "tr") popTo("tr", ["table", "thead", "tbody", "tfoot"]);
    else if (name === "td" || name === "th") {
      if (!popTo("td", ["tr", "table"])) popTo("th", ["tr", "table"]);
    } else if (name === "option") popTo("option", ["select"]);
    else if (name === "tbody" || name === "thead" || name === "tfoot") {
      if (!popTo("tbody", ["table"]) && !popTo("thead", ["table"])) popTo("tfoot", ["table"]);
    }
  };

  const n = source.length;
  let i = 0;
  let textStart = 0;
  const flushText = (end: number) => {
    if (end > textStart) pushText(decodeEntities(source.slice(textStart, end)));
  };

  while (i < n) {
    const lt = source.indexOf("<", i);
    if (lt < 0) break;
    i = lt;
    if (source.startsWith("<!--", i)) {
      flushText(i);
      const end = source.indexOf("-->", i + 4);
      i = end < 0 ? n : end + 3;
      textStart = i;
      continue;
    }
    if (source.startsWith("<![CDATA[", i)) {
      flushText(i);
      const end = source.indexOf("]]>", i + 9);
      pushText(source.slice(i + 9, end < 0 ? n : end));
      i = end < 0 ? n : end + 3;
      textStart = i;
      continue;
    }
    if (source[i + 1] === "!" || source[i + 1] === "?") {
      flushText(i);
      i = skipDeclaration(source, i);
      textStart = i;
      continue;
    }
    if (source[i + 1] === "/") {
      const m = /^<\/\s*([^\s>/]+)\s*>?/.exec(source.slice(i, i + 300));
      if (!m || !NAME_START.test(m[1][0])) {
        i++;
        continue;
      }
      flushText(i);
      const name = xml ? m[1] : m[1].toLowerCase();
      i += m[0].length;
      textStart = i;
      if (!xml && name === "br") {
        top().children.push({ type: "el", name: "br", attrs: [], children: [] });
        continue;
      }
      popTo(name);
      continue;
    }
    if (!NAME_START.test(source[i + 1] ?? "")) {
      i++;
      continue;
    }
    // An opening tag: name, attributes, optional "/".
    const tag = readTag(source, i, xml);
    if (!tag) {
      i++;
      continue;
    }
    flushText(i);
    i = tag.end;
    textStart = i;
    implicitClose(tag.name);
    const element: MElement = { type: "el", name: tag.name, attrs: tag.attrs, children: [] };
    if (stack.length > MAX_DEPTH) {
      // Absurd nesting: flatten instead of recursing forever later.
      top().children.push(element);
      continue;
    }
    top().children.push(element);
    if (tag.selfClosing || (!xml && VOID_ELEMENTS.has(tag.name))) continue;
    if (!xml && RAW_TEXT.has(tag.name)) {
      const close = new RegExp(`</${tag.name}\\s*>`, "gi");
      close.lastIndex = i; // search from here, without copying the rest of the document
      const m = close.exec(source);
      const end = m ? m.index : n;
      const raw = source.slice(i, end);
      if (raw) element.children.push({ type: "text", text: tag.name === "title" || tag.name === "textarea" ? decodeEntities(raw) : raw });
      i = m ? end + m[0].length : n;
      textStart = i;
      continue;
    }
    stack.push(element);
  }
  flushText(n);
  return root;
}

function skipDeclaration(source: string, i: number): number {
  if (source[i + 1] === "?") {
    const end = source.indexOf("?>", i + 2);
    if (end >= 0) return end + 2;
    const gt = source.indexOf(">", i + 2);
    return gt < 0 ? source.length : gt + 1;
  }
  // <!DOCTYPE ... [ internal subset ] > : skip the subset as a whole, entity declarations included.
  let j = i + 2;
  let depth = 0;
  let quote = "";
  for (; j < source.length; j++) {
    const c = source[j];
    if (quote) {
      if (c === quote) quote = "";
      continue;
    }
    if (c === '"' || c === "'") quote = c;
    else if (c === "[") depth++;
    else if (c === "]") depth = Math.max(0, depth - 1);
    else if (c === ">" && depth === 0) return j + 1;
  }
  return source.length;
}

interface Tag {
  name: string;
  attrs: [string, string][];
  selfClosing: boolean;
  end: number;
}

function readTag(s: string, start: number, xml: boolean): Tag | null {
  let i = start + 1;
  const n = s.length;
  let j = i;
  while (j < n && !/[\s/>]/.test(s[j])) j++;
  if (j === i) return null;
  const rawName = s.slice(i, j);
  i = j;
  const attrs: [string, string][] = [];
  const seen = new Set<string>();
  let selfClosing = false;
  while (i < n) {
    while (i < n && /\s/.test(s[i])) i++;
    if (i >= n) break;
    if (s[i] === ">") {
      i++;
      return { name: xml ? rawName : rawName.toLowerCase(), attrs, selfClosing, end: i };
    }
    if (s[i] === "/") {
      selfClosing = s[i + 1] === ">" || selfClosing;
      i++;
      continue;
    }
    if (s[i] === "<") break; // a tag that was never closed: stop it here
    let k = i;
    while (k < n && !/[\s=/>]/.test(s[k])) k++;
    if (k === i) {
      i++;
      continue;
    }
    const attrName = xml ? s.slice(i, k) : s.slice(i, k).toLowerCase();
    i = k;
    while (i < n && /\s/.test(s[i])) i++;
    let value = "";
    if (s[i] === "=") {
      i++;
      while (i < n && /\s/.test(s[i])) i++;
      const q = s[i];
      if (q === '"' || q === "'") {
        const end = s.indexOf(q, i + 1);
        value = s.slice(i + 1, end < 0 ? n : end);
        i = end < 0 ? n : end + 1;
      } else {
        let e = i;
        while (e < n && !/[\s>]/.test(s[e])) e++;
        value = s.slice(i, e);
        i = e;
      }
    }
    if (!seen.has(attrName)) {
      seen.add(attrName);
      attrs.push([attrName, decodeEntities(value)]);
    }
  }
  return { name: xml ? rawName : rawName.toLowerCase(), attrs, selfClosing, end: i };
}

// ---- reading helpers ----------------------------------------------------------------------------------------------------------------

export function localName(name: string): string {
  const c = name.indexOf(":");
  return c >= 0 ? name.slice(c + 1) : name;
}

export function el(name: string, attrs: [string, string][] = [], children: MNode[] = []): MElement {
  return { type: "el", name, attrs, children };
}

export function text(value: string): MText {
  return { type: "text", text: value };
}

/** An attribute by its exact name, or (when `byLocal`) by its local name ("l:href" matches "href"). */
export function attr(e: MElement, name: string, byLocal = false): string | undefined {
  for (const [k, v] of e.attrs) if (k === name || (byLocal && localName(k) === name)) return v;
  return undefined;
}

export function setAttr(e: MElement, name: string, value: string): void {
  const a = e.attrs.find(([k]) => k === name);
  if (a) a[1] = value;
  else e.attrs.push([name, value]);
}

export function removeAttr(e: MElement, name: string): void {
  e.attrs = e.attrs.filter(([k]) => k !== name);
}

export function elements(nodes: readonly MNode[]): MElement[] {
  return nodes.filter((c): c is MElement => c.type === "el");
}

/** Child elements with this local name. */
export function childrenNamed(e: MElement, local: string): MElement[] {
  return elements(e.children).filter((c) => localName(c.name) === local);
}

export function childNamed(e: MElement | undefined, local: string): MElement | undefined {
  return e ? elements(e.children).find((c) => localName(c.name) === local) : undefined;
}

/** Every element below `e` in document order (iterative: deep books never overflow the stack). */
export function descendants(e: MElement | readonly MNode[]): MElement[] {
  const out: MElement[] = [];
  const stack: MNode[] = [...(Array.isArray(e) ? e : (e as MElement).children)].reverse();
  while (stack.length) {
    const node = stack.pop()!;
    if (node.type !== "el") continue;
    out.push(node);
    for (let i = node.children.length - 1; i >= 0; i--) stack.push(node.children[i]);
  }
  return out;
}

export function firstDescendant(e: MElement | readonly MNode[], local: string): MElement | undefined {
  return descendants(e).find((d) => localName(d.name) === local);
}

/** The text of a node and everything below it. */
export function textContent(node: MNode | readonly MNode[]): string {
  if (Array.isArray(node)) return node.map((c) => textContent(c)).join("");
  const n = node as MNode;
  if (n.type === "text") return n.text;
  let s = "";
  const stack: MNode[] = [...n.children].reverse();
  while (stack.length) {
    const c = stack.pop()!;
    if (c.type === "text") s += c.text;
    else for (let i = c.children.length - 1; i >= 0; i--) stack.push(c.children[i]);
  }
  return s;
}

/** Visits every element below the given nodes; `visit` may replace an element by returning a node list (its replacement). */
export function rewrite(nodes: MNode[], visit: (e: MElement) => MNode[] | undefined): MNode[] {
  const out: MNode[] = [];
  for (const node of nodes) {
    if (node.type === "text") {
      out.push(node);
      continue;
    }
    const replacement = visit(node);
    if (replacement !== undefined) {
      out.push(...replacement);
      continue;
    }
    node.children = rewrite(node.children, visit);
    out.push(node);
  }
  return out;
}

/** The <body> element of a parsed document, or the document itself for a fragment. */
export function bodyOf(doc: MElement): MElement {
  return firstDescendant(doc, "body") ?? doc;
}

// ---- writing ------------------------------------------------------------------------------------------------------------------------

export interface WriteOptions {
  /** XHTML / XML: void elements written as `<br/>`, text cleaned of characters XML forbids. Default: HTML. */
  xml?: boolean;
  /** Plain XML (not XHTML): every empty element is written as `<x/>`. */
  selfCloseEmpty?: boolean;
}

const XML_NAME = /^[A-Za-z_][A-Za-z0-9._-]*$/;

/** Serialises nodes. In XML mode an element whose name is not a plain XML name (e.g. "o:p") is unwrapped: its children are kept. */
export function serialize(nodes: readonly MNode[], options: WriteOptions = {}): string {
  const xml = options.xml === true;
  const parts: string[] = [];
  const walk = (list: readonly MNode[]) => {
    for (const node of list) {
      if (node.type === "text") {
        parts.push(escapeText(xml ? stripInvalidXml(node.text) : node.text));
        continue;
      }
      if (xml && !XML_NAME.test(node.name)) {
        walk(node.children);
        continue;
      }
      parts.push("<", node.name);
      for (const [k, v] of node.attrs) {
        if (xml && !/^[A-Za-z_][A-Za-z0-9._:-]*$/.test(k)) continue;
        parts.push(" ", k, '="', escapeAttr(xml ? stripInvalidXml(v) : v), '"');
      }
      const isVoid = VOID_ELEMENTS.has(node.name) || (xml && options.selfCloseEmpty === true);
      if (isVoid && node.children.length === 0) {
        parts.push(xml ? "/>" : ">");
        continue;
      }
      parts.push(">");
      walk(node.children);
      parts.push("</", node.name, ">");
    }
  };
  walk(nodes);
  return parts.join("");
}
