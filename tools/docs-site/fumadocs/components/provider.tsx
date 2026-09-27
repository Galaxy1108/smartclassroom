'use client';
import SearchDialog from '@/components/search';
import { RootProvider } from 'fumadocs-ui/provider/next';
import { TranslationProvider } from '@fuma-translate/react';
import { type ReactNode } from 'react';

// Fumadocs 的界面文案（搜索框、目录、翻页…）走 @fuma-translate/react，
// 这里统一覆盖成中文 —— 与 SnowLuma 文档站的观感一致。
const zh = {
  // 注意：Fumadocs 的文案键带"上下文后缀"，形如 "Search(search trigger)"
  'Search(search trigger)': '搜索',
  'Open Search(search trigger)(aria-label)': '打开搜索',
  'Search(search dialog)': '搜索',
  'Close Search(search dialog)(aria-label)': '关闭搜索',
  'No results found(search dialog)': '没有找到相关内容',
  'On this page(table of contents)': '本页目录',
  'No Headings(table of contents)': '本页没有小标题',
  'Table of Contents(inline table of contents)': '本页目录',
  'Previous Page(pagination)': '上一页',
  'Next Page(pagination)': '下一页',
  'Choose a language(language switcher)': '选择语言',
  'Toggle Theme(theme switcher)(aria-label)': '切换主题',
  'Light(theme switcher)(aria-label)': '浅色',
  'Dark(theme switcher)(aria-label)': '深色',
  'System(theme switcher)(aria-label)': '跟随系统',
  'Edit on GitHub': '在 GitHub 上编辑',
  'Copy Markdown': '复制 Markdown',
  'View as Markdown': '查看 Markdown',
  'Page Not Found': '页面不存在',
  'Back to Home': '返回首页',
  'Last updated': '最后更新于',
};

export function Provider({ children }: { children: ReactNode }) {
  return (
    <TranslationProvider translations={zh}>
      <RootProvider search={{ SearchDialog }}>{children}</RootProvider>
    </TranslationProvider>
  );
}
