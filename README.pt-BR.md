<img alt="TutorialOverlay — lições desenhadas sobre qualquer aplicativo do Windows" src="docs/media/logo.webp" width="100%">

<a href="README.md">English</a> · <a href="README.ru.md">Русский</a> · <a href="README.es.md">Español</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <b>Português</b> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>

[![kizrum/tutorial_overlay MCP server](https://glama.ai/mcp/servers/kizrum/tutorial_overlay/badges/score.svg)](https://glama.ai/mcp/servers/kizrum/tutorial_overlay)

Tutoriais passo a passo desenhados direto sobre qualquer aplicativo do Windows, guiados por um agente de IA.

<img alt="Uma lição no FL Studio: o mascote aponta as células do passo e o cartão marca cada passo" src="docs/media/fl-lesson.webp" width="800" />

A lição acima foi percorrida pelo robô de testes do projeto no FL Studio.

**Mostra onde. Explica por quê.** Agentes que clicam por você num aplicativo terminam a tarefa, mas você não aprende
nada. O TutorialOverlay deixa os cliques com você: aponta cada um e explica por que é o certo, para que da próxima
vez você não precise de ajuda.

Tudo roda no seu computador. O servidor escuta só em `127.0.0.1` e não envia nada para lugar nenhum; ele não grava a
tela e só tira uma captura quando o agente pede ([mais](docs/server.md#screenshots-and-privacy)).

## Como começar

[Instale o TutorialOverlay](#instalação) e pergunte ao seu agente de IA como fazer algo num aplicativo:

> [!TIP]
> *“Me ensine passo a passo a ativar a quebra automática de linha no Bloco de Notas”*

O agente lê a interface do aplicativo por MCP e escreve a lição: passos, alvos e um *por quê* para cada um:

```json
{
  "title": "Quebra de linha no Bloco de Notas",
  "app": "Notepad",
  "lang": "pt-BR",
  "steps": [
    {
      "title": "Abra o menu Exibir",
      "why": "Tudo o que diz respeito a como o texto aparece fica no menu Exibir.",
      "targets": [{ "name": "Exibir", "anchor": { "element": { "name": "Exibir", "role": "MenuItem" } } }]
    },
    {
      "title": "Feche o menu com Esc",
      "until": [{ "type": "key", "keys": "Esc" }]
    }
  ]
}
```

O overlay contorna cada alvo sobre o aplicativo, o mascote aponta para ele e um cartão de passos guia você. Os passos
se concluem sozinhos: um clique, um pixel, uma janela, um atalho, um arrastar, uma rolagem. Travou? Clique em
**Ajuda** e o agente responde direto no cartão. No fim, a lição pode ser salva e refeita pela bandeja, sem o agente.

<img width="420" alt="O cartão de passos: Ajuda marca o alvo e explica, depois Faça tudo — uma etiqueta azul AUTO no topo do cartão, e os passos se marcam sozinhos" src="docs/media/card-help.webp" />

Quer ver feito primeiro? **Faça este passo** (ou **Faça tudo**) no cartão entrega o mouse ao overlay: ele move o
ponteiro de verdade e clica no que está enquadrado enquanto você assiste, e uma etiqueta azul **AUTO** no topo do
cartão mostra que ele está agindo. Mexa no mouse e o controle volta para você na hora. O que não pode ser desfeito
(pagar, fazer pedido, excluir de vez, enviar) ele nunca clica: esse botão fica vermelho com um cadeado, e quem clica
é você.

O cartão, a bandeja e as configurações falam português, inglês, russo, espanhol, alemão, francês, chinês e japonês;
dá para adicionar o seu próprio idioma ([como](docs/server.md)).

O formato completo de uma lição está em [`docs/tutorials.md`](docs/tutorials.md) (em inglês).

## Instalação

> [!NOTE]
> Windows 10 ou 11, x64. Mais nada: o .NET vem dentro do exe.

O TutorialOverlay é um servidor MCP local, então qualquer agente que fale MCP pode controlá-lo. Baixe
`TutorialOverlay-<versão>-win-x64.zip` em [Releases][releases], descompacte (por exemplo em
`C:\Tools\TutorialOverlay`) e coloque no `PATH` como `tutorial-overlay`:

```powershell
Copy-Item C:\Tools\TutorialOverlay\TutorialOverlay.exe C:\Tools\TutorialOverlay\tutorial-overlay.exe
$p = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$p;C:\Tools\TutorialOverlay", 'User')
```

Depois, no **Claude Code**, instale o plugin: o servidor e a skill que ensina o agente a escrever boas lições:

```text
/plugin marketplace add kizrum/tutorial_overlay
/plugin install tutorial-overlay@tutorial-overlay
```

É só isso: sem chave, sem porta. O agente inicia o overlay quando precisa, e ele fica na bandeja até você fechá-lo
pelo menu dela.

> [!TIP]
> O Claude Code com o plugin funciona melhor. Outros agentes recebem as mesmas ferramentas e leem a mesma skill pela
> ferramenta `guide` (desde a 0.7.0), mas sem o hook do plugin que mantém o agente atento durante a lição.

<details>
<summary>Codex</summary>

Adicione em `~/.codex/config.toml` (ou em `.codex/config.toml` num projeto):

```toml
[mcp_servers.tutorial-overlay]
command = "tutorial-overlay"
args = ["--stdio"]
```

</details>

<details>
<summary>Claude Desktop</summary>

No Claude Desktop, abra **Settings → Developer → Edit Config** e adicione o servidor ao `claude_desktop_config.json`
(mantenha as outras entradas de `mcpServers`, se houver):

```json
{
  "mcpServers": {
    "tutorial-overlay": {
      "command": "C:\\Tools\\TutorialOverlay\\TutorialOverlay.exe",
      "args": ["--stdio"]
    }
  }
}
```

Use o caminho completo do exe: o Claude Desktop pode não ver um `PATH` alterado enquanto estava aberto. Feche o
Claude Desktop por completo (fechar a janela pode deixá-lo na bandeja) e abra de novo: as ferramentas aparecem numa
conversa nova. Depois é só pedir: *“Me ensine a ativar a quebra automática de linha no Bloco de Notas.”*

</details>

<details>
<summary>Claude Code sem o plugin, outros clientes MCP</summary>

Adicione um servidor MCP local (stdio): o comando `tutorial-overlay` (ou o caminho completo de `TutorialOverlay.exe`)
com o argumento `--stdio`. No Claude Code:

```powershell
claude mcp add --scope user tutorial-overlay -- tutorial-overlay --stdio
```

Não adicione assim junto com o plugin: o agente veria cada ferramenta duas vezes. Clientes que falam Streamable HTTP
também podem se conectar direto ao overlay em execução: veja [`docs/server.md`](docs/server.md).

</details>

<details>
<summary>Atualizar</summary>

Feche o TutorialOverlay pelo menu da bandeja, descompacte a nova versão sobre a pasta antiga e copie de novo
`TutorialOverlay.exe` como `tutorial-overlay.exe`. As configurações, a chave e os tutoriais salvos ficam em
`%LOCALAPPDATA%\TutorialOverlay` e continuam lá; as configurações do agente também continuam valendo.

</details>

## Compilar a partir do código-fonte

É preciso o [SDK do .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0). Na raiz do repositório:

```powershell
# compilar: publish\TutorialOverlay.exe
dotnet publish -c Release -o publish

# rodar os testes
dotnet run --project tests

# usar o seu build no Claude Code
claude mcp add --scope user tutorial-overlay -- "$PWD\publish\TutorialOverlay.exe" --stdio
```

`.\release.ps1` gera o zip da versão como verificação; o publicado é gerado pelo
[GitHub Actions](docs/development.md#releases) na tag da versão. Como o programa funciona por dentro está em
[`docs/development.md`](docs/development.md).

## Referência (em inglês)

- [Servidor](docs/server.md): opções de linha de comando, o ícone da bandeja, as configurações, `config.json`, a segurança, o log
- [Ferramentas e eventos MCP](docs/tools.md): o que o agente pode chamar e o que recebe
- [Tutoriais](docs/tutorials.md): o formato da lição: formas, âncoras, passos e condições, o mascote, a ajuda
- [Desenvolvimento](docs/development.md): o interior do programa, os testes, o robô de testes

## Licença

[MIT](LICENSE)

***

<img src="docs/media/mascot-outro.webp" width="140" alt="Shazhok, o mascote, dançando" />

Shazhok manda um oi. Como tudo aqui, ele é desenhado em código: sem imagens, sem fontes, sem pacotes.

[releases]: https://github.com/kizrum/tutorial_overlay/releases/latest
