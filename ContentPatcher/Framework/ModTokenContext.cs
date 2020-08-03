using System;
using System.Collections.Generic;
using System.Linq;
using ContentPatcher.Framework.Tokens;
using Pathoschild.Stardew.Common.Utilities;

namespace ContentPatcher.Framework
{
    /// <summary>Manages the token context for a specific content pack.</summary>
    internal class ModTokenContext : IContext
    {
        /*********
        ** Fields
        *********/
        /// <summary>The namespace for tokens specific to this mod.</summary>
        private readonly string Scope;

        /// <summary>The parent token context.</summary>
        private readonly IContext ParentContext;

        /// <summary>The available local standard tokens.</summary>
        private readonly GenericTokenContext LocalContext;

        /// <summary>The dynamic tokens whose value depends on <see cref="DynamicTokenValues"/>.</summary>
        private readonly GenericTokenContext<DynamicToken> DynamicContext;

        /// <summary>The conditional values used to set the values of <see cref="DynamicContext"/> tokens.</summary>
        private readonly IList<DynamicTokenValue> DynamicTokenValues = new List<DynamicTokenValue>();

        /// <summary>The underlying token contexts in priority order.</summary>
        private readonly IContext[] Contexts;

        /// <summary>Maps tokens to those affected by changes to their value in the mod context.</summary>
        private InvariantDictionary<InvariantHashSet> TokenDependents { get; } = new InvariantDictionary<InvariantHashSet>();

        /// <summary>The new tokens which haven't received a context update yet.</summary>
        private readonly InvariantHashSet PendingTokens = new InvariantHashSet();


        /*********
        ** Public methods
        *********/
        /****
        ** Token management
        ****/
        /// <summary>Construct an instance.</summary>
        /// <param name="scope">The namespace for tokens specific to this mod.</param>
        /// <param name="parentContext">The parent token context.</param>
        public ModTokenContext(string scope, IContext parentContext)
        {
            this.Scope = scope;
            this.ParentContext = parentContext;
            this.LocalContext = new GenericTokenContext(this.IsModInstalled);
            this.DynamicContext = new GenericTokenContext<DynamicToken>(this.IsModInstalled);
            this.Contexts = new[] { this.ParentContext, this.LocalContext, this.DynamicContext };
        }

        /// <summary>Add a standard token to the context.</summary>
        /// <param name="token">The config token to add.</param>
        public void Add(IHigherLevelToken<IToken> token)
        {
            if (token.Scope != this.Scope)
                throw new InvalidOperationException($"Can't register the '{token.Name}' mod token because its scope '{token.Scope}' doesn't match this mod scope '{this.Scope}.");
            if (token.Name.Contains(InternalConstants.PositionalInputArgSeparator))
                throw new InvalidOperationException($"Can't register the '{token.Name}' mod token because positional input arguments aren't supported ({InternalConstants.PositionalInputArgSeparator} character).");
            if (this.ParentContext.Contains(token.Name, enforceContext: false))
                throw new InvalidOperationException($"Can't register the '{token.Name}' mod token because there's a global token with that name.");
            if (this.LocalContext.Contains(token.Name, enforceContext: false))
                throw new InvalidOperationException($"The '{token.Name}' token is already registered.");

            this.LocalContext.Save(token);
            this.PendingTokens.Add(token.Name);
        }

        /// <summary>Add a dynamic token value to the context.</summary>
        /// <param name="tokenValue">The token to add.</param>
        public void Add(DynamicTokenValue tokenValue)
        {
            // validate
            if (this.ParentContext.Contains(tokenValue.Name, enforceContext: false))
                throw new InvalidOperationException($"Can't register a '{tokenValue}' token because there's a global token with that name.");
            if (this.LocalContext.Contains(tokenValue.Name, enforceContext: false))
                throw new InvalidOperationException($"Can't register a '{tokenValue.Name}' dynamic token because there's a config token with that name.");

            // get (or create) token
            DynamicToken token;
            {
                if (!this.DynamicContext.Tokens.TryGetValue(tokenValue.Name, out IHigherLevelToken<DynamicToken> wrapper))
                    this.DynamicContext.Save(wrapper = new HigherLevelTokenWrapper<DynamicToken>(new DynamicToken(tokenValue.Name, this.Scope)));
                token = wrapper.Token;
            }

            // add token value
            token.AddTokensUsed(tokenValue.GetTokensUsed());
            token.AddAllowedValues(tokenValue.Value);
            this.DynamicTokenValues.Add(tokenValue);

            // track tokens which should trigger an update to this token
            Queue<string> tokenQueue = new Queue<string>(tokenValue.GetTokensUsed());
            InvariantHashSet visited = new InvariantHashSet();
            while (tokenQueue.Any())
            {
                // get token name
                string tokenName = tokenQueue.Dequeue();
                if (!visited.Add(tokenName))
                    continue;

                // if the current token uses other tokens, they may affect the being added too
                IToken curToken = this.GetToken(tokenName, enforceContext: false);
                foreach (string name in curToken.GetTokensUsed())
                    tokenQueue.Enqueue(name);
                if (curToken is DynamicToken curDynamicToken)
                {
                    foreach (string name in curDynamicToken.GetPossibleTokensUsed())
                        tokenQueue.Enqueue(name);
                }

                // add dynamic value as a dependency of the current token
                if (!this.TokenDependents.TryGetValue(curToken.Name, out InvariantHashSet used))
                    this.TokenDependents.Add(curToken.Name, used = new InvariantHashSet());
                used.Add(tokenValue.Name);
            }
        }

        /// <summary>Update the current context.</summary>
        /// <param name="globalChangedTokens">The global token values which changed.</param>
        public void UpdateContext(InvariantHashSet globalChangedTokens)
        {
            // nothing to do
            if (!globalChangedTokens.Any() && !this.PendingTokens.Any())
                return;

            // get dynamic tokens which need an update
            InvariantHashSet affectedTokens = this.GetTokensToUpdate(globalChangedTokens);
            if (!affectedTokens.Any())
                return;

            // update local standard tokens
            foreach (var token in this.LocalContext.Tokens.Values)
            {
                if (token.IsMutable && affectedTokens.Contains(token.Name))
                    token.UpdateContext(this);
            }

            // reset dynamic tokens
            // note: since token values are affected by the order they're defined, only updating tokens affected by globalChangedTokens is not trivial.
            foreach (DynamicToken token in this.DynamicContext.Tokens.Values.Select(p => p.Token))
            {
                token.SetValue(null);
                token.SetReady(false);
            }
            foreach (DynamicTokenValue tokenValue in this.DynamicTokenValues)
            {
                tokenValue.UpdateContext(this);
                if (tokenValue.IsReady && tokenValue.Conditions.All(p => p.IsMatch))
                {
                    DynamicToken token = this.DynamicContext.Tokens[tokenValue.Name].Token;
                    token.SetValue(tokenValue.Value);
                    token.SetReady(true);
                }
            }

            this.PendingTokens.Clear();
        }

        /// <summary>Get the tokens affected by changes to a given token.</summary>
        /// <param name="token">The token name to check.</param>
        public IEnumerable<string> GetTokensAffectedBy(string token)
        {
            return this.TokenDependents.TryGetValue(token, out InvariantHashSet affectedTokens)
                ? affectedTokens
                : Enumerable.Empty<string>();
        }

        /****
        ** IContext
        ****/
        /// <inheritdoc />
        public bool IsModInstalled(string id)
        {
            return this.ParentContext.IsModInstalled(id);
        }

        /// <inheritdoc />
        public bool Contains(string name, bool enforceContext)
        {
            return this.Contexts.Any(p => p.Contains(name, enforceContext));
        }

        /// <inheritdoc />
        public IToken GetToken(string name, bool enforceContext)
        {
            foreach (IContext context in this.Contexts)
            {
                IToken token = context.GetToken(name, enforceContext);
                if (token != null)
                    return token;
            }

            return null;
        }

        /// <inheritdoc />
        public IEnumerable<IToken> GetTokens(bool enforceContext)
        {
            foreach (IContext context in this.Contexts)
            {
                foreach (IToken token in context.GetTokens(enforceContext))
                    yield return token;
            }
        }

        /// <inheritdoc />
        public IEnumerable<string> GetValues(string name, IInputArguments input, bool enforceContext)
        {
            IToken token = this.GetToken(name, enforceContext);
            return token?.GetValues(input) ?? Enumerable.Empty<string>();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get the tokens which need a context update.</summary>
        /// <param name="globalChangedTokens">The global token values which changed.</param>
        private InvariantHashSet GetTokensToUpdate(InvariantHashSet globalChangedTokens)
        {
            // add tokens which depend on a changed global token
            InvariantHashSet affectedTokens = new InvariantHashSet();
            foreach (string globalToken in globalChangedTokens)
            {
                foreach (string affectedToken in this.GetTokensAffectedBy(globalToken))
                    affectedTokens.Add(affectedToken);
            }

            // add uninitialized tokens
            foreach (string patch in this.PendingTokens)
                affectedTokens.Add(patch);

            return affectedTokens;
        }
    }
}
