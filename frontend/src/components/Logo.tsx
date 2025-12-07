import React from 'react';

interface LogoProps {
    collapsed?: boolean;
    className?: string;
    onClick?: () => void;
}

export const Logo: React.FC<LogoProps> = ({ collapsed, className = "", onClick }) => {
    return (
        <div
            onClick={onClick}
            className={`flex items-center gap-3 select-none cursor-pointer group ${className}`}
            title="Toggle Sidebar"
        >
            {/* Icon */}
            <div className="relative w-10 h-10 flex-shrink-0 flex items-center justify-center bg-gradient-to-br from-blue-600 to-purple-600 rounded-xl shadow-lg group-hover:shadow-blue-500/20 transition-all duration-300">
                <svg viewBox="0 0 24 24" fill="none" className="w-6 h-6 text-white" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M9 18V5l12-2v13" />
                    <circle cx="6" cy="18" r="3" />
                    <circle cx="18" cy="16" r="3" />
                </svg>
            </div>

            {/* Text */}
            <div className={`overflow-hidden transition-all duration-300 ${collapsed ? 'w-0 opacity-0' : 'w-auto opacity-100'}`}>
                <h1 className="text-2xl font-bold tracking-tighter text-white whitespace-nowrap">
                    Web<span className="text-blue-500">Music</span>
                </h1>
            </div>
        </div>
    );
};
