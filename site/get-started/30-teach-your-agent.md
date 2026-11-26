---
title: Teach your coding agent
---

The asmsdk plugin gives Claude Code the SDK's rules and recipes, a linter for `.asmproj` projects and a scaffolder.

### Add the marketplace

```claude
/plugin marketplace add AsmFoundation/AsmClaudeMarketplace
```

Type it in Claude Code. The marketplace is called `asmfoundation`; it holds one plugin per topic.

### Install the plugin

```claude
/plugin install asmsdk@asmfoundation
```

The plugin follows AsmSdk's releases: plugin 0.4.0 teaches SDK 0.4.0.

### Ask for a project

```text
Create an AsmSdk library that sums an int array, for Windows and Linux on x64,
with a C# test that calls it.
```

In our evaluation of plugin 0.1.0, agents with the plugin built 12 of 12 test projects; without it, 4 of 12.
