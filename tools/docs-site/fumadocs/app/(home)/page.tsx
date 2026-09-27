import Link from 'next/link';

export default function HomePage() {
  return (
    <div className="flex flex-col items-center justify-center flex-1 gap-6 px-6 text-center">
      <h1 className="text-4xl font-bold">智慧课堂 SmartClassroom</h1>
      <p className="max-w-xl text-fd-muted-foreground">
        QQ 群 / 私聊 → AI 理解 → ClassIsland 提醒、作业墙、换课、课件归档。
        老师在 QQ 里说一句话，教室里的 ClassIsland 就会动起来。
      </p>
      <div className="flex flex-wrap items-center justify-center gap-3">
        <Link
          href="/docs"
          className="rounded-lg bg-fd-primary px-5 py-2.5 font-medium text-fd-primary-foreground"
        >
          阅读文档
        </Link>
        <Link
          href="/docs/qq-snowluma"
          className="rounded-lg border border-fd-border px-5 py-2.5 font-medium"
        >
          QQ 接入
        </Link>
        <Link
          href="/docs/classisland"
          className="rounded-lg border border-fd-border px-5 py-2.5 font-medium"
        >
          ClassIsland 集成
        </Link>
      </div>
      <p className="text-sm text-fd-muted-foreground">代码由 AI 生成 · MIT</p>
    </div>
  );
}
