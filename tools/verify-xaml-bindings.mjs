#!/usr/bin/env node
// Checks that every binding path in the Windows app's XAML names a member that
// exists.
//
// WPF resolves a binding path at run time. A typo compiles cleanly, raises
// nothing, and shows up as an empty control, so the Windows interface has a class
// of defect that neither the compiler nor a unit test can see. The interface is
// also written on macOS, where WPF cannot run at all. This closes that gap on any
// platform: it reads the XAML, reads the viewmodels, and fails when a path has
// nowhere to land.
//
// It is deliberately conservative. When it cannot work out what a binding
// resolves against, it says nothing rather than guessing, because a false failure
// here would train people to ignore it. Everything it does report is a path whose
// owning type is known and whose first hop is genuinely absent.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';

const repoRoot = path.resolve(import.meta.dirname, '..');
const appRoot = path.join(repoRoot, 'windows/src/Patchthrough.App');
const sourceRoots = [path.join(repoRoot, 'windows/src')];

// The types a DataTemplate can declare, mapped from its XAML namespace prefix.
const DATA_TYPES = {
  'vm:SessionItemViewModel': 'SessionItemViewModel',
  'vm:SessionGroupViewModel': 'SessionGroupViewModel',
  'vm:SessionDetailViewModel': 'SessionDetailViewModel',
  'vm:DestinationViewModel': 'DestinationViewModel',
  'vm:DestinationGroupViewModel': 'DestinationGroupViewModel',
  'core:Turn': 'Turn',
  'core:ResolvedNote': 'ResolvedNote',
};

// What a window's own bindings resolve against, which is its DataContext.
const WINDOW_CONTEXT = {
  'MainWindow.xaml': 'ShellViewModel',
  'SettingsWindow.xaml': 'SettingsViewModel',
};

// The context a `DataContext.X` binding reaches. Every one of these in the app
// walks up to the window, whose context is the shell.
const ANCESTOR_CONTEXT = 'ShellViewModel';

// Binding's own settable properties. As the first token one of these is a named
// argument rather than a path: `{Binding ElementName=Chevron}` binds nothing.
const BINDING_KEYWORDS = new Set([
  'ElementName', 'RelativeSource', 'Source', 'Converter', 'Mode', 'Path',
  'UpdateSourceTrigger', 'StringFormat', 'FallbackValue', 'TargetNullValue',
  'Delay', 'ConverterParameter', 'IsAsync', 'NotifyOnValidationError',
  'ValidatesOnDataErrors', 'XPath',
]);

function walk(directory, extension, found = []) {
  for (const entry of readdirSync(directory)) {
    const full = path.join(directory, entry);
    if (entry === 'obj' || entry === 'bin') continue;
    if (statSync(full).isDirectory()) walk(full, extension, found);
    else if (entry.endsWith(extension)) found.push(full);
  }
  return found;
}

const TYPE_DECLARATION =
  /^\s*(?:public|internal)?\s*(?:sealed |static |abstract |partial |readonly )*(?:class|record|enum|struct)\s+(\w+)/;
// A public member: a property with any body shape, a field, an event, or a method.
const PUBLIC_MEMBER =
  /^\s*public\s+(?:static\s+|virtual\s+|override\s+|readonly\s+|required\s+|abstract\s+|sealed\s+|event\s+|new\s+)*(?:[\w<>?[\],.]+(?:\s*<[^>]*>)?)\s+(\w+)\s*(?:\{|=>|;|\(|$)/;
const ENUM_VALUE = /^\s*([A-Z]\w*)\s*(?:,|=|$)/;
// A property whose type is another named type, so a dotted path can be followed.
const TYPED_PROPERTY = /^\s*public\s+(?:static\s+)?([A-Z]\w+)\??\s+(\w+)\s*(?:\{|=>|;|$)/;

/** Public members per type, and the declared type of each property. */
function readSources() {
  const members = new Map();
  const propertyType = new Map();
  const add = (type, name) => {
    if (!members.has(type)) members.set(type, new Set());
    members.get(type).add(name);
  };

  for (const root of sourceRoots) {
    for (const file of walk(root, '.cs')) {
      const text = readFileSync(file, 'utf8');

      // Positional record parameters, which are public members too.
      for (const match of text.matchAll(/record\s+(\w+)\s*\(([^)]*)\)/gs)) {
        for (const part of match[2].split(',')) {
          const name = part.trim().split('=')[0].trim().split(/\s+/).pop();
          if (name && /^[A-Za-z]/.test(name)) add(match[1], name);
        }
      }

      let current = null;
      let inEnum = false;
      for (const line of text.split('\n')) {
        const declaration = TYPE_DECLARATION.exec(line);
        if (declaration) {
          current = declaration[1];
          inEnum = / enum /.test(line);
          continue;
        }
        if (!current) continue;
        if (inEnum) {
          const value = ENUM_VALUE.exec(line);
          if (value) add(current, value[1]);
          continue;
        }
        const member = PUBLIC_MEMBER.exec(line);
        if (member) add(current, member[1]);
        const typed = TYPED_PROPERTY.exec(line);
        if (typed) propertyType.set(`${current}.${typed[2]}`, typed[1]);
      }
    }
  }
  return { members, propertyType };
}

/**
 * Every `<DataTemplate …>` with the body its own closing tag encloses.
 *
 * The nesting has to be counted rather than matched with a lazy pattern, because
 * templates nest and because `<DataTemplate.Triggers>` is a property element
 * rather than another template.
 */
function templates(text) {
  const found = [];
  const OPEN = /<DataTemplate(?=[\s/>])([^>]*?)(\/?)>/g;
  for (const open of text.matchAll(OPEN)) {
    if (open[2] === '/') continue;
    let depth = 1;
    let index = open.index + open[0].length;
    while (depth > 0 && index < text.length) {
      const next = /<DataTemplate(?=[\s/>])[^>]*?(\/?)>|<\/DataTemplate\s*>/.exec(text.slice(index));
      if (!next) break;
      const token = next[0];
      index += next.index + token.length;
      if (token.startsWith('</')) depth -= 1;
      else if (!token.endsWith('/>')) depth += 1;
    }
    found.push({ head: open[1], body: text.slice(open.index + open[0].length, index) });
  }
  return found;
}

/** The body with any nested template removed, since a nested one owns its bindings. */
function withoutNested(body) {
  let result = body;
  for (const nested of templates(body)) result = result.replace(nested.body, '');
  return result;
}

const BINDING = /\{Binding\s+(?:Path=)?([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)([^}]*)\}/g;

function main() {
  const { members, propertyType } = readSources();
  const problems = [];

  /** Report only when the owner is known and a hop is genuinely missing. */
  const check = (owner, pathText, where) => {
    let type = owner;
    const hops = pathText.split('.');
    for (const hop of hops) {
      if (!members.has(type)) return;   // unknown type: not ours to judge
      if (!members.get(type).has(hop)) {
        problems.push(`${where}: '${pathText}' needs ${type}.${hop}, which does not exist`);
        return;
      }
      const next = propertyType.get(`${type}.${hop}`);
      if (!next) return;                // cannot follow further, so stop here
      type = next;
    }
  };

  const files = walk(appRoot, '.xaml');
  for (const file of files) {
    const name = path.basename(file);
    const text = readFileSync(file, 'utf8');

    for (const template of templates(text)) {
      const declared = /DataType="\{x:Type\s+([\w:]+)\}"/.exec(template.head);
      if (!declared) continue;
      const owner = DATA_TYPES[declared[1]];
      if (!owner || !members.has(owner)) continue;
      const body = withoutNested(template.body);
      for (const binding of body.matchAll(BINDING)) {
        const [, bindingPath, rest] = binding;
        if (BINDING_KEYWORDS.has(bindingPath)) continue;
        if (/RelativeSource|ElementName/.test(rest)) continue;
        if (bindingPath.startsWith('DataContext')) continue;
        check(owner, bindingPath, `${name} template<${owner}>`);
      }
    }

    const windowOwner = WINDOW_CONTEXT[name];
    if (windowOwner) {
      let outside = text;
      for (const template of templates(text)) outside = outside.replace(template.body, '');
      for (const binding of outside.matchAll(BINDING)) {
        const [, bindingPath, rest] = binding;
        if (BINDING_KEYWORDS.has(bindingPath)) continue;
        if (/RelativeSource|ElementName/.test(rest)) continue;
        check(windowOwner, bindingPath, name);
      }
    }

    for (const binding of text.matchAll(/\{Binding\s+DataContext\.([A-Za-z_][\w.]*)/g)) {
      check(ANCESTOR_CONTEXT, binding[1], `${name} (ancestor context)`);
    }
  }

  if (problems.length > 0) {
    console.error('XAML binding paths that resolve to nothing:\n');
    for (const problem of problems.sort()) console.error(`  ${problem}`);
    console.error(
      '\nWPF resolves these at run time, so the control would simply render empty.'
    );
    process.exit(1);
  }
  console.log(`verify-xaml-bindings: ${files.length} XAML files, every binding path resolves`);
}

main();
