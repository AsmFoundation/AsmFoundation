// Highlight.js grammars of the foundation's own. site/build.cs loads this file after vendor/highlight.min.js
// and vendor/languages/x86asm.min.js, inside a JavaScript engine at build time; nothing here reaches a browser.
//   nasm    x86asm plus what NASM and AsmSdk add: the conditional and macro directives x86asm lacks, and the
//           macros of AsmSdk's asmsdk.inc (FUNCTION, ENDFUNCTION, RETURN, ARG1 to ARG6)
//   cli     one command line, as typed after a prompt: the program, its options, and numbers and versions
//           (bash's grammar leaves a line like `docker run --rm -p 50051:50051 ...` nearly uncolored)
//   claude  a Claude Code slash command: the command, then its repository or plugin arguments
"use strict";
(function () {
  /**
   * NASM as AsmSdk projects write it.
   * @param {object} hljs The highlight.js instance (unused: x86asm's own definition is reused).
   * @returns {object} The grammar: a fresh copy of x86asm's, with keywords added.
   * @throws {Error} When x86asm is not registered: vendor/languages/x86asm.min.js must load first.
   */
  function nasm(hljs) {
    const x86 = hljs.getLanguage("x86asm");
    if (!x86) throw new Error("nasm extends x86asm: load vendor/languages/x86asm.min.js first");
    // rawDefinition builds a fresh grammar object, so editing it leaves x86asm itself unchanged
    const g = x86.rawDefinition();
    g.name = "NASM (AsmSdk)";
    g.aliases = ["asm"];
    g.keywords = Object.assign({}, g.keywords, {
      // the grammar is case-insensitive, so these match however a source spells them
      meta: g.keywords.meta + " %ifdef %ifndef %elifdef %elifndef %ifnmacro %macro %endmacro %imacro %unmacro FUNCTION ENDFUNCTION",
      built_in: g.keywords.built_in + " ARG1 ARG2 ARG3 ARG4 ARG5 ARG6",
      keyword: g.keywords.keyword + " RETURN"
    });
    // a dotted name (.text, .rodata, .note.GNU-stack) is one plain name; without this, x86asm's number rule
    // colors a lone "." in it as a number. Comments, strings and numbers start earlier on their lines, so
    // they still win where they apply
    g.contains = [{ match: /\.[A-Za-z_$?][\w.$?@-]*/ }].concat(g.contains);
    return g;
  }

  /**
   * One command line, as typed after a shell prompt.
   * @param {object} hljs The highlight.js instance, for its shared string and comment modes.
   * @returns {object} The grammar.
   */
  function cli(hljs) {
    return {
      name: "Command line",
      contains: [
        hljs.HASH_COMMENT_MODE,
        hljs.QUOTE_STRING_MODE,
        hljs.APOS_STRING_MODE,
        // the program: the first word of the line
        { scope: "built_in", match: /^[A-Za-z_.\/][\w.\/-]*/ },
        // options: -p, --rm, --target, standing after a space
        { scope: "attr", match: /(?<=\s)--?[A-Za-z][\w-]*/ },
        // numbers, ports and versions: 50051, 0.3.0, standing after a space, a colon or an equals sign
        { scope: "number", match: /(?<=[\s:=])\d+(?:\.\d+)*\b/ }
      ]
    };
  }

  /**
   * A Claude Code slash command.
   * @returns {object} The grammar.
   */
  function claude() {
    return {
      name: "Claude Code command",
      contains: [
        // the command: /plugin, /model ...
        { scope: "keyword", match: /^\/[a-z][\w-]*/ },
        // plugin@marketplace and owner/repository arguments
        { scope: "string", match: /[\w.-]+@[\w.-]+|[A-Za-z][\w.-]*\/[\w.-]+/ }
      ]
    };
  }

  hljs.registerLanguage("nasm", nasm);
  hljs.registerLanguage("cli", cli);
  hljs.registerLanguage("claude", claude);
})();
