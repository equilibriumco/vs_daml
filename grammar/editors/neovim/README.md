# Daml in Neovim

The grammar's own queries (`grammar/tree-sitter-daml/queries/`) are written for Neovim: its
capture names, and the last matching pattern wins. Neovim has no `daml` filetype yet, so every
setup below starts by adding it:

```lua
vim.filetype.add({ extension = { daml = 'daml' } })
```

## With nvim-treesitter (`main` branch, Neovim 0.12)

nvim-treesitter compiles the grammar with the tree-sitter CLI (0.26.1 or later on `PATH`). Point
it at an unpacked `tree-sitter-daml-<version>.tar.gz` from a tree-sitter-daml release, which holds
the generated parser and the queries:

```lua
vim.api.nvim_create_autocmd('User', {
  pattern = 'TSUpdate',
  callback = function()
    require('nvim-treesitter.parsers').daml = {
      install_info = {
        path = '/path/to/tree-sitter-daml-0.1.0',
        queries = 'queries',
      },
    }
  end,
})

vim.api.nvim_create_autocmd('FileType', {
  pattern = 'daml',
  callback = function()
    vim.treesitter.start()
    vim.wo[0][0].foldexpr = 'v:lua.vim.treesitter.foldexpr()'
    vim.wo[0][0].foldmethod = 'expr'
    vim.bo.indentexpr = "v:lua.require'nvim-treesitter'.indentexpr()"
  end,
})
```

Then `:TSInstall daml`. Indentation comes from nvim-treesitter (`indents.scm`), so it is only
there with it.

## Without nvim-treesitter (Neovim 0.11 or later)

1. Build the parser in the unpacked archive with `tree-sitter build -o daml.so` (any platform),
   or take `daml_ts.dll` from a `daml_ts-<version>-windows-<arch>.zip` of the release: it exports
   `tree_sitter_daml`, which is all Neovim looks for.
2. Copy the archive's `queries/*.scm` to `queries/daml/` in your config folder
   (`~/.config/nvim/`, on Windows `%LocalAppData%\nvim\`).
3. In your config:

```lua
vim.treesitter.language.add('daml', { path = '/path/to/daml.so' })
vim.api.nvim_create_autocmd('FileType', {
  pattern = 'daml',
  callback = function()
    vim.treesitter.start()
    vim.wo[0][0].foldexpr = 'v:lua.vim.treesitter.foldexpr()'
    vim.wo[0][0].foldmethod = 'expr'
  end,
})
```

## Language server

Neovim 0.11's built-in client can start the Daml SDK's server the way the Daml extensions for VS
Code and Visual Studio do (`daml` in place of `dpm` without dpm; `ide` in place of `multi-ide` for
a single package):

```lua
vim.lsp.config('damlc', {
  cmd = { 'dpm', 'damlc', 'multi-ide' },
  filetypes = { 'daml' },
  root_markers = { 'multi-package.yaml', 'daml.yaml' },
})
vim.lsp.enable('damlc')
```

## Colours

Daml's contract keywords (`with`, `choice`, `controller`, `signatory`, …) are captured as
`@keyword.contract` and its declarations (`template`, `interface`, `exception`, `data`, …) as
`@keyword.declaration`; both fall back to `@keyword` unless a colour scheme sets them, for
example `vim.api.nvim_set_hl(0, '@keyword.contract.daml', { link = 'Statement' })`.
