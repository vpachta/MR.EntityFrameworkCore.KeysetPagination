﻿using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace MR.EntityFrameworkCore.KeysetPagination;

public static class KeysetPaginationExtensions
{
	private static readonly MethodInfo StringCompareToMethod = GetCompareToMethod(typeof(string));
	private static readonly MethodInfo GuidCompareToMethod = GetCompareToMethod(typeof(Guid));
	private static readonly ConstantExpression ConstantExpression0 = Expression.Constant(0);

	/// <summary>
	/// Paginates using keyset pagination.
	/// </summary>
	/// <typeparam name="T">The type of the elements of source.</typeparam>
	/// <param name="source">An <see cref="IQueryable{T}"/> to paginate.</param>
	/// <param name="builderAction">An action that takes a builder and registers the columns upon which keyset pagination will work.</param>
	/// <param name="direction">The direction to take. Default is Forward.</param>
	/// <param name="reference">The reference object. Needs to have properties with exact names matching the configured properties. Doesn't necessarily need to be the same type as T.</param>
	/// <returns>An object containing the modified queryable. Can be used with other helper methods related to keyset pagination.</returns>
	/// <exception cref="ArgumentNullException">source or builderAction is null.</exception>
	/// <exception cref="InvalidOperationException">If no properties were registered with the builder.</exception>
	/// <remarks>
	/// Note that calling this method will override any OrderBy calls you have done before.
	/// </remarks>
	public static KeysetPaginationContext<T> KeysetPaginate<T>(
		this IQueryable<T> source,
		Action<KeysetPaginationBuilder<T>> builderAction,
		KeysetPaginationDirection direction = KeysetPaginationDirection.Forward,
		object? reference = null)
		where T : class
	{
		if (source == null)
		{
			throw new ArgumentNullException(nameof(source));
		}
		if (builderAction == null)
		{
			throw new ArgumentNullException(nameof(builderAction));
		}

		var builder = new KeysetPaginationBuilder<T>();
		builderAction(builder);
		var items = builder.Items;

		if (!items.Any())
		{
			throw new InvalidOperationException("There should be at least one property you're acting on.");
		}

		// Order

		var orderedQuery = items[0].ApplyOrderBy(source, direction);
		for (var i = 1; i < items.Count; i++)
		{
			orderedQuery = items[i].ApplyThenOrderBy(orderedQuery, direction);
		}

		// Predicate

		var predicateQuery = orderedQuery.AsQueryable();
		if (reference != null)
		{
			var keysetPredicateLambda = BuildKeysetPredicateExpression(items, direction, reference);
			predicateQuery = predicateQuery.Where(keysetPredicateLambda);
		}

		return new KeysetPaginationContext<T>(predicateQuery, orderedQuery, items);
	}

	/// <summary>
	/// Paginates using keyset pagination.
	/// </summary>
	/// <typeparam name="T">The type of the elements of source.</typeparam>
	/// <param name="source">An <see cref="IQueryable{T}"/> to paginate.</param>
	/// <param name="builderAction">An action that takes a builder and registers the columns upon which keyset pagination will work.</param>
	/// <param name="direction">The direction to take. Default is Forward.</param>
	/// <param name="reference">The reference object. Needs to have properties with exact names matching the configured properties. Doesn't necessarily need to be the same type as T.</param>
	/// <returns>The modified the queryable.</returns>
	/// <exception cref="ArgumentNullException">source or builderAction is null.</exception>
	/// <exception cref="InvalidOperationException">If no properties were registered with the builder.</exception>
	/// <remarks>
	/// Note that calling this method will override any OrderBy calls you have done before.
	/// </remarks>
	public static IQueryable<T> KeysetPaginateQuery<T>(
		this IQueryable<T> source,
		Action<KeysetPaginationBuilder<T>> builderAction,
		KeysetPaginationDirection direction = KeysetPaginationDirection.Forward,
		object? reference = null)
		where T : class
	{
		return KeysetPaginate(source, builderAction, direction, reference).Query;
	}

	/// <summary>
	/// Returns true when there is more data before the list.
	/// </summary>
	/// <typeparam name="T">The type of the elements of source.</typeparam>
	/// <typeparam name="T2">The type of the elements of the list.</typeparam>
	/// <param name="context">The <see cref="KeysetPaginationContext{T}"/> object.</param>
	/// <param name="items">The list of items.</param>
	public static Task<bool> HasPreviousAsync<T, T2>(
		this KeysetPaginationContext<T> context,
		List<T2> items)
		where T : class
	{
		if (items == null)
		{
			throw new ArgumentNullException(nameof(items));
		}
		if (context == null)
		{
			throw new ArgumentNullException(nameof(context));
		}

		if (!items.Any())
		{
			return Task.FromResult(false);
		}

		var reference = items.First()!;
		return HasAsync(context, KeysetPaginationDirection.Backward, reference);
	}

	/// <summary>
	/// Returns true when there is more data after the list.
	/// </summary>
	/// <typeparam name="T">The type of the elements of source.</typeparam>
	/// <typeparam name="T2">The type of the elements of the list.</typeparam>
	/// <param name="context">The <see cref="KeysetPaginationContext{T}"/> object.</param>
	/// <param name="items">The list of items.</param>
	public static Task<bool> HasNextAsync<T, T2>(
		this KeysetPaginationContext<T> context,
		List<T2> items)
		where T : class
	{
		if (items == null)
		{
			throw new ArgumentNullException(nameof(items));
		}
		if (context == null)
		{
			throw new ArgumentNullException(nameof(context));
		}

		if (!items.Any())
		{
			return Task.FromResult(false);
		}

		var reference = items.Last()!;
		return HasAsync(context, KeysetPaginationDirection.Forward, reference);
	}

	private static Task<bool> HasAsync<T>(
		this KeysetPaginationContext<T> context,
		KeysetPaginationDirection direction,
		object reference)
		where T : class
	{
		var lambda = BuildKeysetPredicateExpression(
			context.Items, direction, reference);
		return context.OrderedQuery.AnyAsync(lambda);
	}

	private static List<object> GetValues<T>(
		IReadOnlyList<KeysetPaginationItem<T>> items,
		object reference)
		where T : class
	{
		var accessor = Accessor.Obtain(reference.GetType());
		var referenceValues = new List<object>(capacity: items.Count);
		foreach (var item in items)
		{
			var value = accessor.GetPropertyValue(reference, item.Property.Name);
			referenceValues.Add(value);
		}
		return referenceValues;
	}

	private static Expression<Func<T, bool>> BuildKeysetPredicateExpression<T>(
		IReadOnlyList<KeysetPaginationItem<T>> items,
		KeysetPaginationDirection direction,
		object reference)
		where T : class
	{
		// A composite keyset pagination in sql looks something like this:
		//   (x, y, ...) > (a, b, ...)
		//
		// The generalized expression for this in pseudocode is:
		//   (x > a) OR
		//   (x = a AND y > b) OR
		//   (x = a AND y = b AND z > c) OR...
		//
		// Of course, this will be a bit more complex when ASC and DESC are mixed.
		// Assume x is ASC, y is DESC, and z is ASC:
		//   (x > a) OR
		//   (x = a AND y < b) OR
		//   (x = a AND y = b AND z > c) OR...

		var referenceValues = GetValues(items, reference);

		// entity =>
		var param = Expression.Parameter(typeof(T), "entity");

		var orExpression = default(BinaryExpression)!;
		var innerLimit = 1;
		// This loop compounds the outer OR expressions.
		for (var i = 0; i < items.Count; i++)
		{
			var andExpression = default(BinaryExpression)!;

			// This loop compounds the inner AND expressions.
			// innerLimit implicitly grows from 1 to items.Count by each iteration.
			for (var j = 0; j < innerLimit; j++)
			{
				var isLast = j + 1 == innerLimit;
				var item = items[j];
				var referenceValueExpression = Expression.Constant(referenceValues[j]);
				var memberAccess = Expression.MakeMemberAccess(param, item.Property);

				BinaryExpression innerExpression;
				if (!isLast)
				{
					innerExpression = Expression.Equal(memberAccess, referenceValueExpression);
				}
				else
				{
					var greaterThan = direction switch
					{
						KeysetPaginationDirection.Forward when !item.IsDescending => true,
						KeysetPaginationDirection.Forward when item.IsDescending => false,
						KeysetPaginationDirection.Backward when !item.IsDescending => false,
						KeysetPaginationDirection.Backward when item.IsDescending => true,
						_ => throw new NotImplementedException(),
					};

					var propertyType = item.Property.PropertyType;
					if (propertyType == typeof(string) || propertyType == typeof(Guid))
					{
						// GreaterThan/LessThan operators are not valid for strings and guids.
						// We use string/Guid.CompareTo instead.

						// entity.Property.CompareTo(constant) >|< 0
						// -----------------------------------------

						// entity.Property.CompareTo(constant)
						var compareToMethod = propertyType == typeof(string) ? StringCompareToMethod : GuidCompareToMethod;
						var methodCallExpression = Expression.Call(memberAccess, compareToMethod, referenceValueExpression);

						innerExpression = greaterThan ?
							Expression.GreaterThan(methodCallExpression, ConstantExpression0) :
							Expression.LessThan(methodCallExpression, ConstantExpression0);
					}
					else
					{
						innerExpression = greaterThan ?
							Expression.GreaterThan(memberAccess, referenceValueExpression) :
							Expression.LessThan(memberAccess, referenceValueExpression);
					}
				}

				andExpression = andExpression == null ? innerExpression : Expression.And(andExpression, innerExpression);
			}

			orExpression = orExpression == null ? andExpression : Expression.Or(orExpression, andExpression);

			innerLimit++;
		}

		return Expression.Lambda<Func<T, bool>>(orExpression, param);
	}

	private static MethodInfo GetCompareToMethod(Type type)
	{
		var methodInfo = type.GetTypeInfo().GetMethod(nameof(string.CompareTo), new Type[] { type });
		if (methodInfo == null)
		{
			throw new InvalidOperationException($"Didn't find a CompareTo method on type {type.Name}.");
		}

		return methodInfo;
	}
}
